-- seed-metadata.sql
-- Idempotent INSERT of all metadata tables for a fresh MAIA database.
-- Run AFTER schema-create.sql, BEFORE any job seed scripts.
--
-- Deploy sequence:
--   1. schema-create.sql     (CREATE TABLE + indexes)
--   2. seed-metadata.sql     (this file)
--   3. seed-nsg-events.sql   (production job example)

USE [MaiaDB];
GO

-- ============================================================
-- ScanTypes
-- ScanTypeId is an IDENTITY column; specific IDs are required
-- because ScanSources.ScanTypeId FK references them by value.
-- ============================================================
SET IDENTITY_INSERT [dbo].[ScanTypes] ON;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ScanTypes] WHERE [ScanTypeId] = 1)
    INSERT INTO [dbo].[ScanTypes] ([ScanTypeId], [Name], [Description], [LeaseDurationSeconds])
    VALUES (1, N'FileSystem', N'Scan log files in a folder matching glob patterns', 300);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ScanTypes] WHERE [ScanTypeId] = 2)
    INSERT INTO [dbo].[ScanTypes] ([ScanTypeId], [Name], [Description], [LeaseDurationSeconds])
    VALUES (2, N'Database', N'Query a SQL table and check column values against rules', 1800);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ScanTypes] WHERE [ScanTypeId] = 3)
    INSERT INTO [dbo].[ScanTypes] ([ScanTypeId], [Name], [Description], [LeaseDurationSeconds])
    VALUES (3, N'ApiEndpoint', N'Poll an HTTP endpoint and inspect the response', 60);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ScanTypes] WHERE [ScanTypeId] = 4)
    INSERT INTO [dbo].[ScanTypes] ([ScanTypeId], [Name], [Description], [LeaseDurationSeconds])
    VALUES (4, N'FileContent', N'Structured extraction from input data files (XML, ...)', 300);

SET IDENTITY_INSERT [dbo].[ScanTypes] OFF;

-- ============================================================
-- JobTypes
-- JobTypeId is an IDENTITY column; IDs 1-4 are canonical and
-- referenced by ClassificationRules and FixPolicyRules.
-- ============================================================
SET IDENTITY_INSERT [dbo].[JobTypes] ON;

IF NOT EXISTS (SELECT 1 FROM [dbo].[JobTypes] WHERE [JobTypeId] = 1)
    INSERT INTO [dbo].[JobTypes] ([JobTypeId], [Name], [Description], [IsActive])
    VALUES (1, N'DTSX', N'SQL Server Integration Services package', 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[JobTypes] WHERE [JobTypeId] = 2)
    INSERT INTO [dbo].[JobTypes] ([JobTypeId], [Name], [Description], [IsActive])
    VALUES (2, N'SqlAgent', N'SQL Server Agent Job', 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[JobTypes] WHERE [JobTypeId] = 3)
    INSERT INTO [dbo].[JobTypes] ([JobTypeId], [Name], [Description], [IsActive])
    VALUES (3, N'Python', N'Python script', 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[JobTypes] WHERE [JobTypeId] = 4)
    INSERT INTO [dbo].[JobTypes] ([JobTypeId], [Name], [Description], [IsActive])
    VALUES (4, N'PowerShell', N'PowerShell script', 1);

SET IDENTITY_INSERT [dbo].[JobTypes] OFF;

-- Exe: identity-assigned ID (not fixed), resolved by name in seed-nsg-events.sql
IF NOT EXISTS (SELECT 1 FROM [dbo].[JobTypes] WHERE [Name] = N'Exe')
    INSERT INTO [dbo].[JobTypes] ([Name], [Description], [IsActive])
    VALUES (N'Exe', N'Native executable / batch process', 1);

-- ============================================================
-- Roles
-- RoleId is NOT an identity column (ValueGeneratedNever).
-- Values must match the MaiaRole enum: User=1, Operator=2, Administrator=3.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[Roles] WHERE [RoleId] = 1)
    INSERT INTO [dbo].[Roles] ([RoleId], [Name]) VALUES (1, N'User');

IF NOT EXISTS (SELECT 1 FROM [dbo].[Roles] WHERE [RoleId] = 2)
    INSERT INTO [dbo].[Roles] ([RoleId], [Name]) VALUES (2, N'Operator');

IF NOT EXISTS (SELECT 1 FROM [dbo].[Roles] WHERE [RoleId] = 3)
    INSERT INTO [dbo].[Roles] ([RoleId], [Name]) VALUES (3, N'Administrator');

-- ============================================================
-- Users  (bootstrap admin — forced password change on first login)
-- Password: admin  |  Change immediately after first login.
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[Users] WHERE [Username] = N'admin')
    INSERT INTO [dbo].[Users]
        ([Username], [PasswordHash], [RoleId], [IsActive], [MustChangePassword], [CreatedAt])
    VALUES
        (
            N'admin',
            N'AQAAAAIAAYagAAAAEPnp2IHUgzfCWpWZjNEACdO0lM/CvWnIaW0l8KlxiWw58i93pgNCH9Hu1YAYpn+fpg==',
            3,              -- Administrator
            1,              -- IsActive = true
            1,              -- MustChangePassword = true (forced rotation on first login)
            GETDATE()
        );
