-- ============================================================
-- MAIA upgrade-existing-to-20261012.sql
-- Brings an EXISTING MAIA database (installed from schema-create.sql, the
-- July 2026 deployment) up to the current schema - latest migration
-- 20261014200000_AllowNewDbConditionKinds - without losing data.
--
-- For a NEW, empty database use schema-clean.sql + seed-metadata.sql instead.
--
-- What it applies (diffed against the July schema-create.sql):
--   1. 20260718182136_AddCompositeInvariantCheckConstraints - 3 CHECK constraints
--   2. 20261012162748_AddDetailColumnsToScanCheckRule        - ScanCheckRules.DetailColumns
--   3. 20261012171955_AddScanCheckRuleConditions             - ScanCheckRuleConditions table
--   3b. 20261014145610_AddScanCheckRuleIdToJobFailure        - JobFailures.ScanCheckRuleId
--   3c. 20261014200000_AllowNewDbConditionKinds              - condition Kind allow-list + NotEquals/Contains/InList
--   4. __EFMigrationsHistory - created + all 35 migrations marked applied, so the
--      next upgrade can be a plain `dotnet ef migrations script --idempotent`.
--
-- Idempotent: every step checks for its object first - safe to re-run, including
-- after a partial failure. The pre-flight block aborts BEFORE any change if the
-- database is older than the July baseline or holds rows the new CHECK
-- constraints would reject.
--
-- Run against the MAIA database (USE it / pick it in SSMS / sqlcmd -d).
-- sqlcmd users: pass -I (QUOTED_IDENTIFIER ON) and -b (non-zero exit code on
-- ABORT), e.g.  sqlcmd -S <server> -d MaiaDb -E -I -b -i upgrade-existing-to-20261012.sql
-- Back up the database first.
-- ============================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

-- ── Pre-flight ──────────────────────────────────────────────────────────────
-- Two batches on purpose: the data checks below reference tables a non-MAIA
-- database lacks, and a missing table kills its whole batch before any later
-- SET NOEXEC ON could run. So the baseline check gets a batch of its own.
-- NOEXEC ON = every following batch is compiled but NOT executed (SSMS + sqlcmd).

-- Baseline: the July schema (auth tables + ReferenceId columns, 2026-06-27).
IF OBJECT_ID(N'dbo.ScanSources') IS NULL OR OBJECT_ID(N'dbo.Users') IS NULL
   OR OBJECT_ID(N'dbo.FixPolicyRuleSteps') IS NULL
   OR COL_LENGTH(N'dbo.JobFailures', N'ReferenceId') IS NULL
   OR COL_LENGTH(N'dbo.ScanCheckRules', N'ReferenceIdColumn') IS NULL
BEGIN
    DECLARE @db sysname = DB_NAME();
    RAISERROR(N'ABORT: database [%s] is not a MAIA database at the July 2026 baseline (ScanSources / Users / FixPolicyRuleSteps / ReferenceId columns missing). Nothing was changed.', 16, 1, @db);
    SET NOEXEC ON;
END
GO

-- Data that would violate the new composite-policy CHECK constraints.
DECLARE @fail bit = 0;
IF OBJECT_ID(N'CK_FixPolicyRuleSteps_ActionType_NotManualOrComposite') IS NULL
   AND EXISTS (SELECT 1 FROM dbo.FixPolicyRuleSteps WHERE ActionType IN ('Manual', 'Composite'))
BEGIN
    SELECT 'FixPolicyRuleSteps: step ActionType must not be Manual/Composite' AS Problem, StepId, RuleId, StepOrder, ActionType
    FROM dbo.FixPolicyRuleSteps WHERE ActionType IN ('Manual', 'Composite');
    RAISERROR(N'ABORT: composite steps with ActionType Manual/Composite exist (listed above). Fix or delete them, then re-run. Nothing was changed.', 16, 1);
    SET @fail = 1;
END
IF @fail = 0 AND OBJECT_ID(N'CK_FixPolicyRuleSteps_ActionPayload_NotEmpty') IS NULL
   AND EXISTS (SELECT 1 FROM dbo.FixPolicyRuleSteps WHERE LEN(LTRIM(RTRIM(ISNULL(ActionPayload, '')))) = 0)
BEGIN
    SELECT 'FixPolicyRuleSteps: step ActionPayload is blank' AS Problem, StepId, RuleId, StepOrder, ActionType
    FROM dbo.FixPolicyRuleSteps WHERE LEN(LTRIM(RTRIM(ISNULL(ActionPayload, '')))) = 0;
    RAISERROR(N'ABORT: composite steps with a blank ActionPayload exist (listed above). Fix or delete them, then re-run. Nothing was changed.', 16, 1);
    SET @fail = 1;
END
IF @fail = 0 AND OBJECT_ID(N'CK_FixPolicyRules_CompositeHeader_NullPayload') IS NULL
   AND EXISTS (SELECT 1 FROM dbo.FixPolicyRules WHERE ActionType = 'Composite' AND ActionPayload IS NOT NULL)
BEGIN
    SELECT 'FixPolicyRules: Composite header carries a payload' AS Problem, RuleId, ActionType, ActionPayload
    FROM dbo.FixPolicyRules WHERE ActionType = 'Composite' AND ActionPayload IS NOT NULL;
    RAISERROR(N'ABORT: Composite policies with a header payload exist (listed above). Set ActionPayload = NULL on them, then re-run. Nothing was changed.', 16, 1);
    SET @fail = 1;
END

-- Stop every following batch (works in SSMS and sqlcmd) - reset at the end.
IF @fail = 1 SET NOEXEC ON;
ELSE PRINT N'Pre-flight OK.';
GO

-- ── 1. AddCompositeInvariantCheckConstraints (2026-07-18) ───────────────────
IF OBJECT_ID(N'CK_FixPolicyRuleSteps_ActionType_NotManualOrComposite') IS NULL
BEGIN
    ALTER TABLE [dbo].[FixPolicyRuleSteps] WITH CHECK
        ADD CONSTRAINT [CK_FixPolicyRuleSteps_ActionType_NotManualOrComposite]
        CHECK ([ActionType] NOT IN ('Manual', 'Composite'));
    PRINT N'Added CK_FixPolicyRuleSteps_ActionType_NotManualOrComposite.';
END
GO
IF OBJECT_ID(N'CK_FixPolicyRuleSteps_ActionPayload_NotEmpty') IS NULL
BEGIN
    ALTER TABLE [dbo].[FixPolicyRuleSteps] WITH CHECK
        ADD CONSTRAINT [CK_FixPolicyRuleSteps_ActionPayload_NotEmpty]
        CHECK (LEN(LTRIM(RTRIM([ActionPayload]))) > 0);
    PRINT N'Added CK_FixPolicyRuleSteps_ActionPayload_NotEmpty.';
END
GO
IF OBJECT_ID(N'CK_FixPolicyRules_CompositeHeader_NullPayload') IS NULL
BEGIN
    ALTER TABLE [dbo].[FixPolicyRules] WITH CHECK
        ADD CONSTRAINT [CK_FixPolicyRules_CompositeHeader_NullPayload]
        CHECK ([ActionType] <> 'Composite' OR [ActionPayload] IS NULL);
    PRINT N'Added CK_FixPolicyRules_CompositeHeader_NullPayload.';
END
GO

-- ── 2. AddDetailColumnsToScanCheckRule (2026-10-12) ─────────────────────────
IF COL_LENGTH(N'dbo.ScanCheckRules', N'DetailColumns') IS NULL
BEGIN
    ALTER TABLE [dbo].[ScanCheckRules] ADD [DetailColumns] nvarchar(500) NULL;
    PRINT N'Added ScanCheckRules.DetailColumns.';
END
GO

-- ── 3. AddScanCheckRuleConditions (2026-10-12) ──────────────────────────────
IF OBJECT_ID(N'dbo.ScanCheckRuleConditions') IS NULL
BEGIN
    CREATE TABLE [dbo].[ScanCheckRuleConditions] (
        [ConditionId] int NOT NULL IDENTITY,
        [CheckRuleId] int NOT NULL,
        [ConditionOrder] int NOT NULL,
        [Kind] nvarchar(50) NOT NULL,
        [TargetField] nvarchar(500) NOT NULL,
        [ExpectedValue] nvarchar(500) NULL,
        [MinValue] decimal(18,4) NULL,
        [MaxValue] decimal(18,4) NULL,
        [PredicateType] nvarchar(50) NULL,
        [Description] nvarchar(500) NULL,
        CONSTRAINT [PK_ScanCheckRuleConditions] PRIMARY KEY ([ConditionId]),
        CONSTRAINT [FK_ScanCheckRuleConditions_ScanCheckRules_CheckRuleId] FOREIGN KEY ([CheckRuleId]) REFERENCES [dbo].[ScanCheckRules] ([CheckRuleId]) ON DELETE CASCADE
    );
    PRINT N'Created table ScanCheckRuleConditions.';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ScanCheckRuleConditions_CheckRuleId_ConditionOrder'
                                          AND object_id = OBJECT_ID(N'dbo.ScanCheckRuleConditions'))
BEGIN
    CREATE UNIQUE INDEX [UX_ScanCheckRuleConditions_CheckRuleId_ConditionOrder]
        ON [dbo].[ScanCheckRuleConditions] ([CheckRuleId], [ConditionOrder]);
    PRINT N'Created index UX_ScanCheckRuleConditions_CheckRuleId_ConditionOrder.';
END
GO
IF OBJECT_ID(N'CK_ScanCheckRuleConditions_Kind_Allowed') IS NULL
BEGIN
    ALTER TABLE [dbo].[ScanCheckRuleConditions] WITH CHECK
        ADD CONSTRAINT [CK_ScanCheckRuleConditions_Kind_Allowed]
        CHECK ([Kind] IN ('ValueEquals', 'ColumnRange', 'NotEquals', 'Contains', 'InList', 'FileContent', 'ErrorKeyword'));
    PRINT N'Added CK_ScanCheckRuleConditions_Kind_Allowed.';
END
GO

-- ── 3b. AddScanCheckRuleIdToJobFailure (2026-10-14) ─────────────────────────
-- Which rule produced the failure (View Source lookup). No FK on purpose.
IF COL_LENGTH(N'dbo.JobFailures', N'ScanCheckRuleId') IS NULL
BEGIN
    ALTER TABLE [dbo].[JobFailures] ADD [ScanCheckRuleId] int NULL;
    PRINT N'Added JobFailures.ScanCheckRuleId.';
END
GO

-- ── 3c. AllowNewDbConditionKinds (2026-10-14) ───────────────────────────────
-- A database that already has the 2026-10-12 constraint gets the widened list.
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = N'CK_ScanCheckRuleConditions_Kind_Allowed'
             AND definition NOT LIKE N'%InList%')
BEGIN
    ALTER TABLE [dbo].[ScanCheckRuleConditions] DROP CONSTRAINT [CK_ScanCheckRuleConditions_Kind_Allowed];
    ALTER TABLE [dbo].[ScanCheckRuleConditions] WITH CHECK
        ADD CONSTRAINT [CK_ScanCheckRuleConditions_Kind_Allowed]
        CHECK ([Kind] IN ('ValueEquals', 'ColumnRange', 'NotEquals', 'Contains', 'InList', 'FileContent', 'ErrorKeyword'));
    PRINT N'Widened CK_ScanCheckRuleConditions_Kind_Allowed (NotEquals / Contains / InList).';
END
GO

-- ── 4. EF migration history - mark the schema as fully migrated ─────────────
IF OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NULL
BEGIN
    CREATE TABLE [dbo].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
    PRINT N'Created table __EFMigrationsHistory.';
END
GO
INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
SELECT m.MigrationId, N'8.0.0'
FROM (VALUES
    (N'20260428151731_AddMonitoredJobs'),
    (N'20260428160543_SeedTrapInterfaces'),
    (N'20260428221938_AddFixActionType'),
    (N'20260428222603_SeedTrapInterfacesFixPolicy'),
    (N'20260429000001_RefactorMonitoredJobLogPath'),
    (N'20260429142339_AddScanTypeToMonitoredJob'),
    (N'20260429144919_NormalizeMonitoredJobScanStructure'),
    (N'20260429152137_MoveSourceTableToScanCheckRule'),
    (N'20260429161005_AddScanWatermarks'),
    (N'20260430113644_AddScanDbWatermarks'),
    (N'20260430125619_RenameFileNameToSourceId'),
    (N'20260430130812_AddSourceIdColumnToScanCheckRule'),
    (N'20260524072845_AddMonitoredJobLeases'),
    (N'20260525155446_AddScanRunHistory'),
    (N'20260528161609_AuditLogConfigSupport'),
    (N'20260530211923_AddFixPolicyActiveKeyUniqueIndex'),
    (N'20260531152630_AddFixPolicyMonitoredJobOverride'),
    (N'20260601160417_CompositeFixesAndSourceFilePath'),
    (N'20260601182726_RecommendationAtomicClaim'),
    (N'20260606130932_AddRuleSuggestionProvenance'),
    (N'20260606145536_AddClassificationRuleActiveKeyIndex'),
    (N'20260607180904_AddFileContentScan'),
    (N'20260609171814_AddPredicateUnevaluableSkips'),
    (N'20260609193221_AddScanSourceEntity'),
    (N'20260611130806_WidenSourceTableForSqlQuery'),
    (N'20260617185954_Tier25CleanupColumns'),
    (N'20260619072202_AddAuthTables'),
    (N'20260627182335_CleanupDevSeedsAddExeJobType'),
    (N'20260627193557_AlignSeedModelRemoveDemoData'),
    (N'20260627220102_ReinstateReferenceIdColumns'),
    (N'20260718182136_AddCompositeInvariantCheckConstraints'),
    (N'20261012162748_AddDetailColumnsToScanCheckRule'),
    (N'20261012171955_AddScanCheckRuleConditions'),
    (N'20261014145610_AddScanCheckRuleIdToJobFailure'),
    (N'20261014200000_AllowNewDbConditionKinds')
) AS m (MigrationId)
WHERE NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory] h WHERE h.MigrationId = m.MigrationId);
PRINT N'Migration history rows added: ' + CAST(@@ROWCOUNT AS nvarchar(10)) + N'.';
GO

PRINT N'Upgrade complete - schema is at 20261014200000_AllowNewDbConditionKinds.';
GO

SET NOEXEC OFF;
GO
