-- =============================================================================
-- Altrata Copilot Connector — SQL Server state backend provisioning
-- =============================================================================
-- Idempotent by construction: every CREATE is wrapped in an existence guard, so
-- the script is safe to re-run (upgrades, docker-compose init, CI double-pass).
--
-- This file is the CANONICAL copy of the DDL embedded in the connector
-- (State/SqlStateStore.SchemaScript and Identity/SqlServerIdentityStore
-- .SchemaScript, which auto-provision on first use). The offline validation
-- suite (tests/SqlScriptValidationTests.cs) parses this file under the real
-- SQL Server grammar, checks idempotency guards, builds a DacFx semantic
-- model, and fails on any drift between this file and the embedded constants.
--
-- Usage:
--   sqlcmd -S <server> -i scripts/sql/create-database.sql
-- =============================================================================

:setvar DatabaseName "AltrataConnector"

IF DB_ID(N'$(DatabaseName)') IS NULL
    CREATE DATABASE [$(DatabaseName)];
GO

USE [$(DatabaseName)];
GO

-- ---------------------------------------------------------------------------
-- State backend (State/SqlStateStore.cs :: SchemaScript)
-- ---------------------------------------------------------------------------
-- NOTE on dbo.altrata_suppressed.subject_id, below: it carries an EXPLICIT
-- binary collation. That table is the DSAR erasure suppression list, and the
-- file backend compares its entries with StringComparer.Ordinal; inheriting the
-- database default (case- AND accent-INSENSITIVE on a stock install) made the
-- two backends disagree about whether a subject had been erased. See
-- SqlStateStore.SubjectIdCollation for the full account.
--
-- The ALTER that follows the CREATE migrates tables provisioned before the
-- collation was pinned, and is a no-op once the column is BIN2. The direction
-- is always safe: a case-insensitive primary key could never have admitted two
-- rows differing only by case, so tightening to binary cannot raise a
-- duplicate-key failure. It does NOT recover erasures that the insensitive key
-- silently swallowed at insert time — re-file those from the erasure ledger
-- after upgrading.
--
-- Comments must stay OUTSIDE the DDL block below: SqlScriptValidationTests
-- asserts the embedded SchemaScript constant appears here contiguously.
IF OBJECT_ID(N'dbo.altrata_checkpoint', N'U') IS NULL
CREATE TABLE dbo.altrata_checkpoint (
    connector_id  NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
    delivery_id   NVARCHAR(256) NOT NULL,
    dataset       NVARCHAR(64)  NOT NULL,
    file_name     NVARCHAR(512) NOT NULL,
    record_index  INT           NOT NULL,
    updated_utc   DATETIME2     NOT NULL
);
IF OBJECT_ID(N'dbo.altrata_deadletter', N'U') IS NULL
CREATE TABLE dbo.altrata_deadletter (
    id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    connector_id  NVARCHAR(64)   COLLATE Latin1_General_100_BIN2 NOT NULL,
    item_id       NVARCHAR(256)  NOT NULL,
    dataset       NVARCHAR(64)   NOT NULL,
    delivery_id   NVARCHAR(256)  NOT NULL,
    error         NVARCHAR(MAX)  NOT NULL,
    op            NVARCHAR(16)   NOT NULL CONSTRAINT df_altrata_dl_op DEFAULT N'upsert',
    correlation_id NVARCHAR(128) NULL,
    payload_json  NVARCHAR(MAX)  NOT NULL,
    failed_utc    DATETIME2      NOT NULL,
    attempts      INT            NOT NULL,
    redacted        BIT           NOT NULL CONSTRAINT df_altrata_dl_redacted DEFAULT 0,
    subject_ids     NVARCHAR(MAX) NOT NULL CONSTRAINT df_altrata_dl_subject_ids DEFAULT N'[]',
    subject_hashes  NVARCHAR(MAX) NOT NULL CONSTRAINT df_altrata_dl_subject_hashes DEFAULT N'[]'
);
IF COL_LENGTH(N'dbo.altrata_deadletter', N'op') IS NULL
    ALTER TABLE dbo.altrata_deadletter
        ADD op NVARCHAR(16) NOT NULL CONSTRAINT df_altrata_dl_op_mig DEFAULT N'upsert';
IF COL_LENGTH(N'dbo.altrata_deadletter', N'redacted') IS NULL
    ALTER TABLE dbo.altrata_deadletter
        ADD redacted       BIT           NOT NULL CONSTRAINT df_altrata_dl_redacted_mig DEFAULT 0,
            subject_ids    NVARCHAR(MAX) NOT NULL CONSTRAINT df_altrata_dl_subject_ids_mig DEFAULT N'[]',
            subject_hashes NVARCHAR(MAX) NOT NULL CONSTRAINT df_altrata_dl_subject_hashes_mig DEFAULT N'[]';
IF COL_LENGTH(N'dbo.altrata_deadletter', N'correlation_id') IS NULL
    ALTER TABLE dbo.altrata_deadletter ADD correlation_id NVARCHAR(128) NULL;
IF OBJECT_ID(N'dbo.altrata_kv', N'U') IS NULL
CREATE TABLE dbo.altrata_kv (
    connector_id  NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    [key]         NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [value]       NVARCHAR(MAX) NULL,
    CONSTRAINT pk_altrata_kv PRIMARY KEY (connector_id, [key])
);
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.altrata_kv')
             AND name IN (N'connector_id', N'key')
             AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    ALTER TABLE dbo.altrata_kv DROP CONSTRAINT pk_altrata_kv;
    ALTER TABLE dbo.altrata_kv
        ALTER COLUMN connector_id NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.altrata_kv
        ALTER COLUMN [key] NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.altrata_kv
        ADD CONSTRAINT pk_altrata_kv PRIMARY KEY (connector_id, [key]);
END
IF OBJECT_ID(N'dbo.altrata_deliveries', N'U') IS NULL
CREATE TABLE dbo.altrata_deliveries (
    connector_id  NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    delivery_id   NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    processed_utc DATETIME2     NOT NULL,
    CONSTRAINT pk_altrata_deliveries PRIMARY KEY (connector_id, delivery_id)
);
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.altrata_deliveries')
             AND name IN (N'connector_id', N'delivery_id')
             AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    ALTER TABLE dbo.altrata_deliveries DROP CONSTRAINT pk_altrata_deliveries;
    ALTER TABLE dbo.altrata_deliveries
        ALTER COLUMN connector_id NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.altrata_deliveries
        ALTER COLUMN delivery_id NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.altrata_deliveries
        ADD CONSTRAINT pk_altrata_deliveries PRIMARY KEY (connector_id, delivery_id);
END
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.altrata_deadletter')
             AND name = N'connector_id'
             AND collation_name <> N'Latin1_General_100_BIN2')
    ALTER TABLE dbo.altrata_deadletter
        ALTER COLUMN connector_id NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
IF OBJECT_ID(N'dbo.altrata_leases', N'U') IS NULL
CREATE TABLE dbo.altrata_leases (
    lease_name    NVARCHAR(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
    owner         NVARCHAR(128) NOT NULL,
    expires_utc   DATETIME2     NOT NULL
);
IF OBJECT_ID(N'dbo.altrata_suppressed', N'U') IS NULL
CREATE TABLE dbo.altrata_suppressed (
    connector_id NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    subject_id   NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT pk_altrata_suppressed PRIMARY KEY (connector_id, subject_id)
);
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.altrata_suppressed')
             AND name IN (N'subject_id', N'connector_id')
             AND collation_name <> N'Latin1_General_100_BIN2')
BEGIN
    ALTER TABLE dbo.altrata_suppressed DROP CONSTRAINT pk_altrata_suppressed;
    ALTER TABLE dbo.altrata_suppressed
        ALTER COLUMN subject_id NVARCHAR(256) COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.altrata_suppressed
        ALTER COLUMN connector_id NVARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL;
    ALTER TABLE dbo.altrata_suppressed
        ADD CONSTRAINT pk_altrata_suppressed PRIMARY KEY (connector_id, subject_id);
END

IF OBJECT_ID(N'dbo.altrata_id_seats', N'U') IS NULL
CREATE TABLE dbo.altrata_id_seats (
    connector_id NVARCHAR(64)  NOT NULL,
    kind         NVARCHAR(32)  NOT NULL,
    value        NVARCHAR(512) NOT NULL,
    CONSTRAINT pk_altrata_id_seats PRIMARY KEY (connector_id, kind, value)
);
IF OBJECT_ID(N'dbo.altrata_id_crm_contacts', N'U') IS NULL
CREATE TABLE dbo.altrata_id_crm_contacts (
    connector_id        NVARCHAR(64)  NOT NULL,
    id                  NVARCHAR(256) NOT NULL,
    email_lower         NVARCHAR(512) NULL,
    name_normalized     NVARCHAR(512) NULL,
    employer_normalized NVARCHAR(512) NULL,
    role_normalized     NVARCHAR(512) NULL,
    CONSTRAINT pk_altrata_id_crm PRIMARY KEY (connector_id, id)
);
IF COL_LENGTH(N'dbo.altrata_id_crm_contacts', N'role_normalized') IS NULL
    ALTER TABLE dbo.altrata_id_crm_contacts ADD role_normalized NVARCHAR(512) NULL;
IF OBJECT_ID(N'dbo.altrata_id_crosswalk', N'U') IS NULL
CREATE TABLE dbo.altrata_id_crosswalk (
    connector_id   NVARCHAR(64)  NOT NULL,
    altrata_id     NVARCHAR(256) NOT NULL,
    crm_contact_id NVARCHAR(256) NOT NULL,
    match_rule     NVARCHAR(64)  NOT NULL,
    linked_utc     DATETIME2     NOT NULL,
    CONSTRAINT pk_altrata_id_crosswalk PRIMARY KEY (connector_id, altrata_id)
);
IF OBJECT_ID(N'dbo.altrata_id_items', N'U') IS NULL
CREATE TABLE dbo.altrata_id_items (
    connector_id      NVARCHAR(64)  NOT NULL,
    item_id           NVARCHAR(256) NOT NULL,
    dataset           NVARCHAR(64)  NOT NULL,
    acl_hash          NVARCHAR(128) NOT NULL,
    last_ingested_utc DATETIME2     NOT NULL,
    CONSTRAINT pk_altrata_id_items PRIMARY KEY (connector_id, item_id)
);
IF OBJECT_ID(N'dbo.altrata_id_path_edges', N'U') IS NULL
CREATE TABLE dbo.altrata_id_path_edges (
    connector_id   NVARCHAR(64)  NOT NULL,
    person_a       NVARCHAR(256) NOT NULL,
    person_a_name  NVARCHAR(512) NULL,
    person_b       NVARCHAR(256) NOT NULL,
    person_b_name  NVARCHAR(512) NULL,
    strength       FLOAT         NOT NULL,
    intermediaries INT           NOT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_altrata_id_path_edges_a')
    CREATE INDEX ix_altrata_id_path_edges_a ON dbo.altrata_id_path_edges (connector_id, person_a);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_altrata_id_path_edges_b')
    CREATE INDEX ix_altrata_id_path_edges_b ON dbo.altrata_id_path_edges (connector_id, person_b);
IF OBJECT_ID(N'dbo.altrata_id_path_orgs', N'U') IS NULL
CREATE TABLE dbo.altrata_id_path_orgs (
    connector_id NVARCHAR(64)  NOT NULL,
    person_id    NVARCHAR(256) NOT NULL,
    org          NVARCHAR(512) NOT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_altrata_id_path_orgs')
    CREATE INDEX ix_altrata_id_path_orgs ON dbo.altrata_id_path_orgs (connector_id, person_id);
IF OBJECT_ID(N'dbo.altrata_id_item_subjects', N'U') IS NULL
CREATE TABLE dbo.altrata_id_item_subjects (
    connector_id NVARCHAR(64)  NOT NULL,
    item_id      NVARCHAR(256) NOT NULL,
    subject_id   NVARCHAR(256) NOT NULL,
    CONSTRAINT pk_altrata_id_item_subjects PRIMARY KEY (connector_id, item_id, subject_id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_altrata_id_item_subjects')
    CREATE INDEX ix_altrata_id_item_subjects ON dbo.altrata_id_item_subjects (connector_id, subject_id);
GO
