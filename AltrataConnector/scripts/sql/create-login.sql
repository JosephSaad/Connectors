-- =============================================================================
-- Altrata Copilot Connector — least-privilege SQL login (optional)
-- =============================================================================
-- Creates a dedicated login/user for the connector with only the permissions
-- the runtime needs: DML on the connector tables (the schema itself is
-- provisioned by create-database.sql or auto-provisioned on first use, which
-- additionally needs CREATE TABLE once).
--
-- Prefer SQL_USE_MANAGED_IDENTITY=true on Azure; this script covers classic
-- SQL auth on Windows Server. Change the password before running.
-- =============================================================================

:setvar DatabaseName "AltrataConnector"
:setvar LoginName "altrata_connector"

USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(LoginName)')
    CREATE LOGIN [$(LoginName)] WITH PASSWORD = N'CHANGE_ME_Strong!Passw0rd';
GO

USE [$(DatabaseName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(LoginName)')
    CREATE USER [$(LoginName)] FOR LOGIN [$(LoginName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_role_members rm
               JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id
               JOIN sys.database_principals m ON rm.member_principal_id = m.principal_id
               WHERE r.name = N'db_datareader' AND m.name = N'$(LoginName)')
    ALTER ROLE db_datareader ADD MEMBER [$(LoginName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_role_members rm
               JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id
               JOIN sys.database_principals m ON rm.member_principal_id = m.principal_id
               WHERE r.name = N'db_datawriter' AND m.name = N'$(LoginName)')
    ALTER ROLE db_datawriter ADD MEMBER [$(LoginName)];
GO
