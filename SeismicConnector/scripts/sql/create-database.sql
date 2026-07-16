-- =============================================================================
-- Seismic Copilot Connector — SQL Server state backend provisioning
-- =============================================================================
-- Creates the database (when missing) and every state table the connector
-- uses with USE_SQL_SERVER=true (docs/SQL_CONTRACT.md). The script is
-- IDEMPOTENT BY CONSTRUCTION: every DDL statement is guarded (IF ... IS NULL /
-- IF NOT EXISTS), so re-running it is always safe — the CI pipeline runs it
-- twice against a live engine to prove exactly that.
--
-- The connector also auto-provisions these tables on first use
-- (Infrastructure/SqlExecutor.cs); this script exists so operators can
-- provision ahead of time with least-privilege app logins (create-login.sql)
-- and so the offline SQL validation suite has a single validatable source.
-- Keep the table shapes in exact sync with SqlExecutor.SchemaDdl — the test
-- suite fails the build when they drift.
--
-- Usage:
--   sqlcmd -S <server> -d master -i scripts/sql/create-database.sql
-- =============================================================================

:setvar DatabaseName "SeismicConnector"

IF DB_ID(N'$(DatabaseName)') IS NULL
    CREATE DATABASE [$(DatabaseName)];
GO

USE [$(DatabaseName)];
GO

-- ── Sync timestamps (replaces logs/sync_state.json) ─────────────────────────
IF OBJECT_ID('dbo.SyncTimestamps', 'U') IS NULL
    CREATE TABLE dbo.SyncTimestamps (
        ConnectorId NVARCHAR(64) NOT NULL PRIMARY KEY,
        LastSyncUtc DATETIME2 NOT NULL);
GO

-- ── Crawl checkpoints (replaces logs/checkpoint_{id}.json) ───────────────────
IF OBJECT_ID('dbo.Checkpoints', 'U') IS NULL
    CREATE TABLE dbo.Checkpoints (
        ConnectorId NVARCHAR(64) NOT NULL,
        ObjectType  NVARCHAR(128) NOT NULL,
        SinceIso    NVARCHAR(64) NULL,
        ChunkIndex  INT NOT NULL,
        UpdatedUtc  DATETIME2 NOT NULL,
        CONSTRAINT PK_Checkpoints PRIMARY KEY (ConnectorId, ObjectType));
GO

-- ── Dead-letter queue (replaces logs/failed_records_{id}.jsonl) ──────────────
IF OBJECT_ID('dbo.DeadLetter', 'U') IS NULL
    CREATE TABLE dbo.DeadLetter (
        Id            BIGINT IDENTITY PRIMARY KEY,
        ConnectorId   NVARCHAR(64) NOT NULL,
        ItemId        NVARCHAR(256) NOT NULL,
        ObjectType    NVARCHAR(128) NOT NULL,
        Error         NVARCHAR(MAX) NULL,
        RequestBody   NVARCHAR(MAX) NULL,
        ResponseBody  NVARCHAR(MAX) NULL,
        CorrelationId NVARCHAR(64) NULL,
        CreatedUtc    DATETIME2 NOT NULL);
GO

-- Distributed-tracing correlation: add the CorrelationId column to a DeadLetter
-- table created by an earlier version (idempotent — guarded by COL_LENGTH).
IF OBJECT_ID('dbo.DeadLetter', 'U') IS NOT NULL
    AND COL_LENGTH('dbo.DeadLetter', 'CorrelationId') IS NULL
    ALTER TABLE dbo.DeadLetter ADD CorrelationId NVARCHAR(64) NULL;
GO

-- ── Principal mappings (replaces the SQLite principals table) ────────────────
IF OBJECT_ID('dbo.Principals', 'U') IS NULL
    CREATE TABLE dbo.Principals (
        ConnectorId   NVARCHAR(64) NOT NULL,
        SeismicId     NVARCHAR(128) NOT NULL,
        PrincipalType NVARCHAR(16) NOT NULL,
        Email         NVARCHAR(320) NULL,
        EntraId       NVARCHAR(64) NULL,
        DisplayName   NVARCHAR(256) NULL,
        SyncedUtc     DATETIME2 NOT NULL,
        CONSTRAINT PK_Principals PRIMARY KEY (ConnectorId, SeismicId));
GO

-- ── Tracked items (replaces the SQLite tracked_items table) ──────────────────
IF OBJECT_ID('dbo.TrackedItems', 'U') IS NULL
    CREATE TABLE dbo.TrackedItems (
        ConnectorId    NVARCHAR(64) NOT NULL,
        ItemId         NVARCHAR(256) NOT NULL,
        VersionId      NVARCHAR(128) NOT NULL,
        TeamsiteId     NVARCHAR(128) NOT NULL,
        ExpiresUtc     DATETIME2 NULL,
        LastSeenUtc    DATETIME2 NOT NULL,
        Status         NVARCHAR(16) NOT NULL,
        AclFingerprint NVARCHAR(128) NULL,
        CONSTRAINT PK_TrackedItems PRIMARY KEY (ConnectorId, ItemId));
GO

-- Re-ACL support: add the ACL fingerprint column to a TrackedItems table
-- created by an earlier version (idempotent — guarded by COL_LENGTH).
IF OBJECT_ID('dbo.TrackedItems', 'U') IS NOT NULL
    AND COL_LENGTH('dbo.TrackedItems', 'AclFingerprint') IS NULL
    ALTER TABLE dbo.TrackedItems ADD AclFingerprint NVARCHAR(128) NULL;
GO

-- ── HA crawl sessions (close-with-failed-claims semantics, docs/HA.md) ───────
-- Status: open | closed | failed. Exactly one open session per connector
-- (filtered unique index); exactly one node ever wins the open→closed/failed
-- UPDATE and is recorded in ClosedBy.
IF OBJECT_ID('dbo.CrawlSessions', 'U') IS NULL
    CREATE TABLE dbo.CrawlSessions (
        CrawlId     UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        ConnectorId NVARCHAR(64) NOT NULL,
        CrawlKind   NVARCHAR(16) NOT NULL,
        SinceIso    NVARCHAR(64) NULL,
        Status      NVARCHAR(16) NOT NULL,
        OpenedUtc   DATETIME2 NOT NULL,
        OpenedBy    NVARCHAR(128) NOT NULL,
        ClosedUtc   DATETIME2 NULL,
        ClosedBy    NVARCHAR(128) NULL);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CrawlSessions_Open')
    CREATE UNIQUE INDEX UX_CrawlSessions_Open
        ON dbo.CrawlSessions(ConnectorId)
        WHERE Status = 'open';
GO

-- ── HA resource claims (lease/lock rows, docs/HA.md) ─────────────────────────
-- Status: claimed | done | failed. A stale heartbeat (older than
-- HA_CLAIM_TIMEOUT_SECONDS) makes a 'claimed' row stealable.
IF OBJECT_ID('dbo.CrawlClaims', 'U') IS NULL
    CREATE TABLE dbo.CrawlClaims (
        CrawlId      UNIQUEIDENTIFIER NOT NULL,
        ResourceKey  NVARCHAR(256) NOT NULL,
        NodeId       NVARCHAR(128) NOT NULL,
        Status       NVARCHAR(16) NOT NULL,
        ClaimedUtc   DATETIME2 NOT NULL,
        HeartbeatUtc DATETIME2 NOT NULL,
        CONSTRAINT PK_CrawlClaims PRIMARY KEY (CrawlId, ResourceKey));
GO
