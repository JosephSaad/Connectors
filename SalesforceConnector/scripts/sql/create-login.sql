-- Copyright (c) Microsoft Corporation.
-- Licensed under the MIT License.

-- scripts/sql/create-login.sql
-- -----------------------------
-- Minimal-privilege SQL login/user for the Salesforce Copilot Connector.
-- Run AFTER create-database.sql, in SQLCMD mode:
--
--     sqlcmd -S <server> -v SqlPassword="<StrongPasswordHere>" -i create-login.sql
--     sqlcmd -S <server> -v DatabaseName=MyConnectorDb -v LoginName=my_app -v SqlPassword="..." -i create-login.sql
--
-- The application principal receives ONLY:
--   * EXECUTE on the connector stored procedures (all data access goes
--     through them — see docs/SQL_CONTRACT.md), and
--   * SELECT on the operational views.
-- No table-level access is granted.
--
-- For HA, create the login with the SAME SID on every Availability Group
-- replica (or use a contained database user) so failover keeps working.

:setvar DatabaseName SalesforceConnector
:setvar LoginName    salesforce_connector_app
:setvar SqlPassword  CHANGE_ME_StrongPassword!

-- ── Server-level login ────────────────────────────────────────────────────────
USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(LoginName)')
    CREATE LOGIN [$(LoginName)]
        WITH PASSWORD = N'$(SqlPassword)',
             DEFAULT_DATABASE = [$(DatabaseName)],
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
GO

-- ── Database user + role ──────────────────────────────────────────────────────
USE [$(DatabaseName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(LoginName)' AND type IN ('S', 'U'))
    CREATE USER [$(LoginName)] FOR LOGIN [$(LoginName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'SalesforceConnectorApp' AND type = 'R')
    CREATE ROLE [SalesforceConnectorApp];
GO

ALTER ROLE [SalesforceConnectorApp] ADD MEMBER [$(LoginName)];
GO

-- ── Grants: EXECUTE on stored procedures ──────────────────────────────────────
-- Identity store
GRANT EXECUTE ON OBJECT::dbo.usp_UpsertGroup                   TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_DeleteGroup                   TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_GetGroups                     TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_GetMembers                    TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_ReplaceGroupMembers           TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_AddMember                     TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_RemoveMember                  TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_StartSession                  TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_CompleteSession               TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_GetLastSession                TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_GetLastSuccessfulContentCrawl TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_GetCachedFields               TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_SaveCachedFields              TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_ClearFieldCache               TO [SalesforceConnectorApp];
-- Sync state / checkpoints / dead-letter
GRANT EXECUTE ON OBJECT::dbo.usp_ReadLastSync                  TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_WriteLastSync                 TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_ReadCheckpoints               TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_WriteCheckpoint               TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_ClearCheckpoints              TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_AppendDeadLetter              TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_ReadDeadLetter                TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_MarkDeadLetterRetried         TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_ClearDeadLetter               TO [SalesforceConnectorApp];
-- HA coordination
GRANT EXECUTE ON OBJECT::dbo.usp_OpenOrJoinCrawl               TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_ClaimNextObject               TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_HeartbeatClaim                TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_CompleteClaim                 TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_CloseCrawlIfComplete          TO [SalesforceConnectorApp];
GRANT EXECUTE ON OBJECT::dbo.usp_PruneHistory                  TO [SalesforceConnectorApp];
GO

-- ── Grants: SELECT on operational views ───────────────────────────────────────
GRANT SELECT ON OBJECT::dbo.vGroupMemberCounts TO [SalesforceConnectorApp];
GRANT SELECT ON OBJECT::dbo.vLastSessions      TO [SalesforceConnectorApp];
GRANT SELECT ON OBJECT::dbo.vDeadLetterSummary TO [SalesforceConnectorApp];
GRANT SELECT ON OBJECT::dbo.vActiveCrawls      TO [SalesforceConnectorApp];
GO
