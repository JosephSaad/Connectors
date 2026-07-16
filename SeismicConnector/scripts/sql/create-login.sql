-- =============================================================================
-- Seismic Copilot Connector — least-privilege application login
-- =============================================================================
-- Creates a SQL login + database user for the connector with only the rights
-- the state backend needs (SELECT/INSERT/UPDATE/DELETE on the dbo state
-- tables). Run AFTER create-database.sql. Idempotent by construction.
--
--   sqlcmd -S <server> -d master -v AppPassword="<strong password>" \
--          -i scripts/sql/create-login.sql
--
-- With SQL_USE_MANAGED_IDENTITY=true skip this script and instead create a
-- contained user FROM EXTERNAL PROVIDER for the managed identity, granting it
-- the same role membership.
-- =============================================================================

:setvar DatabaseName "SeismicConnector"
:setvar AppLoginName "seismic_app"
:setvar AppPassword  "CHANGE_ME_Strong!Passw0rd"

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(AppLoginName)')
    CREATE LOGIN [$(AppLoginName)] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON;
GO

USE [$(DatabaseName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(AppLoginName)')
    CREATE USER [$(AppLoginName)] FOR LOGIN [$(AppLoginName)];
GO

-- Data-plane rights only: the connector never issues DDL when the schema has
-- been provisioned ahead of time with create-database.sql.
ALTER ROLE db_datareader ADD MEMBER [$(AppLoginName)];
GO
ALTER ROLE db_datawriter ADD MEMBER [$(AppLoginName)];
GO
