-- ============================================================
-- seed-metadata.sql
-- Reference data a fresh MAIA database needs to run. Idempotent:
-- every row is inserted only if missing, so re-running is safe.
--
-- Contains: Roles, bootstrap admin user, ScanTypes, JobTypes,
-- default ErrorTypes. Values mirror MaiaDbContext.SeedData (keep in
-- sync — EF HasData tracks these exact IDs).
--
-- Does NOT contain: monitored jobs, scan sources/rules, classification
-- rules, fix policies (configure via the UI), nor any runtime data
-- (failures, recommendations, fix log, audit, scan history).
--
-- Deploy sequence on a new, empty database:
--   1. schema-clean.sql
--   2. seed-metadata.sql   (this file)
-- Run both against the target database (e.g. MaiaDB).
-- ============================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

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
-- Users  (bootstrap admin)
-- Password: admin — MustChangePassword=1 prompts for a new one at
-- first login. Change it immediately.
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
            1,              -- MustChangePassword = true
            GETDATE()
        );

-- ============================================================
-- ScanTypes
-- Fixed IDs: ScanSources.ScanTypeId references them by value, and
-- LeaseDurationSeconds drives the worker's per-job lease/timeout.
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
    VALUES (4, N'FileContent', N'Structured extraction from input data files (XML, …)', 300);

SET IDENTITY_INSERT [dbo].[ScanTypes] OFF;

-- ============================================================
-- JobTypes
-- Fixed IDs: ClassificationRules and FixPolicyRules key on JobTypeId.
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

IF NOT EXISTS (SELECT 1 FROM [dbo].[JobTypes] WHERE [JobTypeId] = 5)
    INSERT INTO [dbo].[JobTypes] ([JobTypeId], [Name], [Description], [IsActive])
    VALUES (5, N'Exe', N'Native executable / batch process', 1);

SET IDENTITY_INSERT [dbo].[JobTypes] OFF;

-- ============================================================
-- ErrorTypes (generic defaults)
-- Environment-specific error types are configuration — add them via
-- the UI (/config/error-types).
-- ============================================================
SET IDENTITY_INSERT [dbo].[ErrorTypes] ON;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ErrorTypes] WHERE [ErrorTypeId] = 1)
    INSERT INTO [dbo].[ErrorTypes] ([ErrorTypeId], [Code], [DisplayName], [Severity], [IsActive])
    VALUES (1, N'FileNotFound', N'File Not Found', N'High', 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ErrorTypes] WHERE [ErrorTypeId] = 2)
    INSERT INTO [dbo].[ErrorTypes] ([ErrorTypeId], [Code], [DisplayName], [Severity], [IsActive])
    VALUES (2, N'DbConnection', N'Database Connection Error', N'High', 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ErrorTypes] WHERE [ErrorTypeId] = 3)
    INSERT INTO [dbo].[ErrorTypes] ([ErrorTypeId], [Code], [DisplayName], [Severity], [IsActive])
    VALUES (3, N'Timeout', N'Execution Timeout', N'Medium', 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ErrorTypes] WHERE [ErrorTypeId] = 4)
    INSERT INTO [dbo].[ErrorTypes] ([ErrorTypeId], [Code], [DisplayName], [Severity], [IsActive])
    VALUES (4, N'Transform', N'Data Transform Failure', N'Medium', 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ErrorTypes] WHERE [ErrorTypeId] = 5)
    INSERT INTO [dbo].[ErrorTypes] ([ErrorTypeId], [Code], [DisplayName], [Severity], [IsActive])
    VALUES (5, N'Permission', N'Access Denied', N'High', 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ErrorTypes] WHERE [ErrorTypeId] = 6)
    INSERT INTO [dbo].[ErrorTypes] ([ErrorTypeId], [Code], [DisplayName], [Severity], [IsActive])
    VALUES (6, N'Unknown', N'Unknown Error', N'Low', 1);

SET IDENTITY_INSERT [dbo].[ErrorTypes] OFF;

COMMIT TRANSACTION;
GO
