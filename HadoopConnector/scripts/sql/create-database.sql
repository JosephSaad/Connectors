-- =============================================================================
-- BDH Hadoop Copilot Connector — SQL Server state backend provisioning
-- =============================================================================
-- Run once against the database named in SQL_CONNECTION_STRING (for HA, the
-- database must live on an Always On AG and the connection string must point
-- at the AG LISTENER). Idempotent. Contract: docs/SQL_CONTRACT.md.

-- ── State: sync timestamps ───────────────────────────────────────────────────
IF OBJECT_ID('dbo.SyncTimestamps', 'U') IS NULL
CREATE TABLE dbo.SyncTimestamps
(
    ConnectorId  NVARCHAR(64)  NOT NULL PRIMARY KEY,
    LastSyncUtc  DATETIME2(7)  NOT NULL
);

-- ── State: per-object checkpoints ───────────────────────────────────────────
IF OBJECT_ID('dbo.Checkpoints', 'U') IS NULL
CREATE TABLE dbo.Checkpoints
(
    ConnectorId  NVARCHAR(64)   NOT NULL,
    ObjectType   NVARCHAR(128)  NOT NULL,
    SinceIso     NVARCHAR(64)   NULL,
    ChunkIndex   INT            NOT NULL DEFAULT 0,
    CONSTRAINT PK_Checkpoints PRIMARY KEY (ConnectorId, ObjectType)
);

-- ── State: dead-letter queue ─────────────────────────────────────────────────
IF OBJECT_ID('dbo.DeadLetter', 'U') IS NULL
CREATE TABLE dbo.DeadLetter
(
    Id            BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ConnectorId   NVARCHAR(64)   NOT NULL,
    ItemId        NVARCHAR(256)  NOT NULL,
    ObjectType    NVARCHAR(128)  NOT NULL,
    Error         NVARCHAR(MAX)  NULL,
    RequestBody   NVARCHAR(MAX)  NULL,
    ResponseBody  NVARCHAR(MAX)  NULL,
    CorrelationId NVARCHAR(64)   NULL,
    CreatedUtc    DATETIME2(7)   NOT NULL DEFAULT SYSUTCDATETIME()
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DeadLetter_Connector')
    CREATE INDEX IX_DeadLetter_Connector ON dbo.DeadLetter (ConnectorId, Id);
-- Existing-database migration (idempotent): CorrelationId added in round 4.
IF COL_LENGTH('dbo.DeadLetter', 'CorrelationId') IS NULL
    ALTER TABLE dbo.DeadLetter ADD CorrelationId NVARCHAR(64) NULL;

-- ── Identity store: principal mappings ──────────────────────────────────────
IF OBJECT_ID('dbo.Principals', 'U') IS NULL
CREATE TABLE dbo.Principals
(
    ConnectorId    NVARCHAR(64)   NOT NULL,
    SourceId     NVARCHAR(256)  NOT NULL,
    PrincipalType  NVARCHAR(16)   NOT NULL,   -- 'user' | 'group'
    Email          NVARCHAR(320)  NULL,
    EntraId        NVARCHAR(64)   NULL,
    UpdatedUtc     DATETIME2(7)   NOT NULL,
    CONSTRAINT PK_Principals PRIMARY KEY (ConnectorId, SourceId)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Principals_Email')
    CREATE INDEX IX_Principals_Email ON dbo.Principals (ConnectorId, Email);

-- ── Ingested-item inventory (deletion sync / reconcile) ─────────────────────
IF OBJECT_ID('dbo.ItemInventory', 'U') IS NULL
CREATE TABLE dbo.ItemInventory
(
    ConnectorId  NVARCHAR(64)   NOT NULL,
    ItemId       NVARCHAR(256)  NOT NULL,
    ObjectType   NVARCHAR(128)  NOT NULL,
    LastSeenUtc  DATETIME2(7)   NOT NULL,
    CONSTRAINT PK_ItemInventory PRIMARY KEY (ConnectorId, ItemId)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ItemInventory_Object')
    CREATE INDEX IX_ItemInventory_Object ON dbo.ItemInventory (ConnectorId, ObjectType);

-- ── HA coordination: crawl runs ──────────────────────────────────────────────
IF OBJECT_ID('dbo.CrawlRuns', 'U') IS NULL
CREATE TABLE dbo.CrawlRuns
(
    CrawlKey     NVARCHAR(256)  NOT NULL PRIMARY KEY,  -- {connector}|{kind}|{slot}
    ConnectorId  NVARCHAR(64)   NOT NULL,
    Kind         NVARCHAR(16)   NOT NULL,              -- 'full' | 'incremental'
    OpenedBy     NVARCHAR(128)  NOT NULL,
    OpenedAtUtc  DATETIME2(7)   NOT NULL,
    Status       NVARCHAR(16)   NOT NULL,              -- 'open' | 'closed' | 'failed'
    ClosedBy     NVARCHAR(128)  NULL,
    ClosedAtUtc  DATETIME2(7)   NULL
);

-- ── HA coordination: object-type claims (lease/lock pattern) ─────────────────
IF OBJECT_ID('dbo.ObjectClaims', 'U') IS NULL
CREATE TABLE dbo.ObjectClaims
(
    CrawlKey      NVARCHAR(256)  NOT NULL,
    ObjectType    NVARCHAR(128)  NOT NULL,
    NodeId        NVARCHAR(128)  NOT NULL,
    HeartbeatUtc  DATETIME2(7)   NOT NULL,
    Status        NVARCHAR(16)   NOT NULL,             -- 'claimed' | 'completed' | 'failed'
    CONSTRAINT PK_ObjectClaims PRIMARY KEY (CrawlKey, ObjectType)
);

-- ── Operator views ───────────────────────────────────────────────────────────
-- CREATE OR ALTER keeps every module idempotent by construction (re-run safe).
GO
CREATE OR ALTER VIEW dbo.vActiveCrawls AS
SELECT r.CrawlKey, r.ConnectorId, r.Kind, r.OpenedBy, r.OpenedAtUtc,
       c.ObjectType, c.NodeId, c.HeartbeatUtc, c.Status AS ClaimStatus,
       DATEDIFF(second, c.HeartbeatUtc, SYSUTCDATETIME()) AS HeartbeatAgeSeconds
FROM dbo.CrawlRuns r
LEFT JOIN dbo.ObjectClaims c ON c.CrawlKey = r.CrawlKey
WHERE r.Status = 'open';
GO

-- Prune closed/failed crawl history + old dead-letter rows (LOG_RETENTION_DAYS
-- analog). Open crawls are never pruned.
CREATE OR ALTER PROCEDURE dbo.usp_PruneHistory @RetentionDays INT
AS
BEGIN
    SET NOCOUNT ON;
    IF @RetentionDays IS NULL OR @RetentionDays <= 0
        RETURN;
    DECLARE @cutoff DATETIME2(7) = DATEADD(day, -@RetentionDays, SYSUTCDATETIME());
    DELETE c FROM dbo.ObjectClaims c
        JOIN dbo.CrawlRuns r ON r.CrawlKey = c.CrawlKey
        WHERE r.Status IN ('closed', 'failed') AND r.ClosedAtUtc < @cutoff;
    DELETE FROM dbo.CrawlRuns
        WHERE Status IN ('closed', 'failed') AND ClosedAtUtc < @cutoff;
    DELETE FROM dbo.DeadLetter WHERE CreatedUtc < @cutoff;
END
GO
