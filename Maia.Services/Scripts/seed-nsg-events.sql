-- ============================================================
-- Seed: Nsg-Events job (NSG-Events scan fail status)
-- Safe for any environment — no hardcoded IDs.
-- Idempotent: checks by business key before inserting.
-- Run AFTER migrations are applied.
-- ============================================================

USE [MaiaDB];
GO

BEGIN TRANSACTION;

-- ── Resolve metadata IDs by name (never hardcode) ─────────
DECLARE @jobTypeId   INT;
DECLARE @dbScanTypeId INT;

SELECT @jobTypeId    = JobTypeId  FROM [dbo].[JobTypes] WHERE [Name] = N'Exe';
SELECT @dbScanTypeId = ScanTypeId FROM [dbo].[ScanTypes] WHERE [Name] = N'Database';

IF @jobTypeId IS NULL    RAISERROR('JobType "Exe" not found — check seeded metadata.', 16, 1);
IF @dbScanTypeId IS NULL RAISERROR('ScanType "Database" not found — check seeded metadata.', 16, 1);

-- ── Error types (lookup by Code, not ID) ──────────────────
DECLARE @errorType8Id  INT;
DECLARE @errorType18Id INT;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ErrorTypes] WHERE Code = N'FailedStatus')
    INSERT INTO [dbo].[ErrorTypes] (Code, DisplayName, Description, Severity, IsActive)
    VALUES (N'FailedStatus', N'StatusCode Failure', N'הרשומה נמצאת בסטטוס תקול', N'Critical', 1);
SELECT @errorType8Id = ErrorTypeId FROM [dbo].[ErrorTypes] WHERE Code = N'FailedStatus';

IF NOT EXISTS (SELECT 1 FROM [dbo].[ErrorTypes] WHERE Code = N'FailedFeedback')
    INSERT INTO [dbo].[ErrorTypes] (Code, DisplayName, Description, Severity, IsActive)
    VALUES (N'FailedFeedback', N'משוב תקול', N'התקבל מהמסלקה משוב תקול', N'Medium', 1);
SELECT @errorType18Id = ErrorTypeId FROM [dbo].[ErrorTypes] WHERE Code = N'FailedFeedback';

-- ── Monitored job ──────────────────────────────────────────
DECLARE @jobId INT;

IF NOT EXISTS (SELECT 1 FROM [dbo].[MonitoredJobs] WHERE [Name] = N'Nsg-Events')
    INSERT INTO [dbo].[MonitoredJobs]
        (Name, DisplayName, JobTypeId, PollingIntervalSeconds, IsActive, Description, CreatedAt)
    VALUES
        (N'Nsg-Events', N'NSG-Events scan fail status', @jobTypeId, 300, 1,
         N'אירועים תקולים  9100 - סטטוס תקול - תיקון ל 5  9100 סטטוס 22 התראה',
         GETDATE());

SELECT @jobId = MonitoredJobId FROM [dbo].[MonitoredJobs] WHERE [Name] = N'Nsg-Events';

-- ── Lease row (1:1 with MonitoredJob, required by worker) ─
IF NOT EXISTS (SELECT 1 FROM [dbo].[MonitoredJobLeases] WHERE MonitoredJobId = @jobId)
    INSERT INTO [dbo].[MonitoredJobLeases] (MonitoredJobId)
    VALUES (@jobId);

-- ── Scan source ────────────────────────────────────────────
DECLARE @sourceId INT;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ScanSources] WHERE MonitoredJobId = @jobId AND [Name] = N'ScanErrorEvents')
    INSERT INTO [dbo].[ScanSources]
        (MonitoredJobId, Name, ScanTypeId,
         LogFolder, SearchPatterns, InputFolder, IncludeSubfolders,
         ConnectionName, LogSourceUrl, PollingIntervalSeconds, IsActive)
    VALUES
        (@jobId, N'ScanErrorEvents', @dbScanTypeId,
         NULL, NULL, NULL, 0,
         N'b2bTest', NULL, 300, 1);

SELECT @sourceId = ScanSourceId FROM [dbo].[ScanSources]
WHERE MonitoredJobId = @jobId AND [Name] = N'ScanErrorEvents';

-- ── Scan check rules (keyed by source + field + expected value) ───────────────
IF NOT EXISTS (SELECT 1 FROM [dbo].[ScanCheckRules]
               WHERE ScanSourceId = @sourceId AND TargetField = N'EventStatusCode' AND ExpectedValue = N'8')
    INSERT INTO [dbo].[ScanCheckRules]
        (MonitoredJobId, ScanSourceId, CheckType, TargetField, MinValue, MaxValue, ExpectedValue,
         Severity, Description, IsActive, SourceTable, WatermarkColumn, SourceIdColumn,
         FilePathColumn, InputPathPattern, ExtractorType, ExtractorLocator, IdentifierLocator,
         ExtractorPredicateType, ExtractorPredicateValue)
    VALUES
        (@jobId, @sourceId, N'ValueEquals', N'EventStatusCode', NULL, NULL, N'8',
         N'Medium', N'תקלת רשת בשליחת בקשה ליצרן', 1,
         N'dbo.Event', N'UpdateDate', N'id',
         NULL, NULL, NULL, NULL, NULL, NULL, NULL);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ScanCheckRules]
               WHERE ScanSourceId = @sourceId AND TargetField = N'EventStatusCode' AND ExpectedValue = N'9')
    INSERT INTO [dbo].[ScanCheckRules]
        (MonitoredJobId, ScanSourceId, CheckType, TargetField, MinValue, MaxValue, ExpectedValue,
         Severity, Description, IsActive, SourceTable, WatermarkColumn, SourceIdColumn,
         FilePathColumn, InputPathPattern, ExtractorType, ExtractorLocator, IdentifierLocator,
         ExtractorPredicateType, ExtractorPredicateValue)
    VALUES
        (@jobId, @sourceId, N'ValueEquals', N'EventStatusCode', NULL, NULL, N'9',
         N'Medium', N'תקלה בשירות היצרן', 1,
         N'dbo.Event', N'UpdateDate', N'id',
         NULL, NULL, NULL, NULL, NULL, NULL, NULL);

IF NOT EXISTS (SELECT 1 FROM [dbo].[ScanCheckRules]
               WHERE ScanSourceId = @sourceId AND TargetField = N'FileStatusCode' AND ExpectedValue = N'22')
    INSERT INTO [dbo].[ScanCheckRules]
        (MonitoredJobId, ScanSourceId, CheckType, TargetField, MinValue, MaxValue, ExpectedValue,
         Severity, Description, IsActive, SourceTable, WatermarkColumn, SourceIdColumn,
         FilePathColumn, InputPathPattern, ExtractorType, ExtractorLocator, IdentifierLocator,
         ExtractorPredicateType, ExtractorPredicateValue)
    VALUES
        (@jobId, @sourceId, N'ValueEquals', N'FileStatusCode', NULL, NULL, N'22',
         N'Low', N'התקבל מהמסלקה משוב תקול ברמת רשומה על המענה', 1,
         N'dbo.Event', N'UpdateDate', N'id',
         NULL, NULL, NULL, NULL, NULL, NULL, NULL);

-- ── Classification rules (keyed by JobType + Pattern) ─────
DECLARE @rule63Id INT, @rule64Id INT, @rule65Id INT;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ClassificationRules]
               WHERE JobTypeId = @jobTypeId AND Pattern = N'EventStatusCode=8' AND IsActive = 1)
    INSERT INTO [dbo].[ClassificationRules]
        (JobTypeId, ErrorTypeId, Pattern, Confidence, Priority, IsActive)
    VALUES (@jobTypeId, @errorType8Id, N'EventStatusCode=8', 0.90, 1, 1);
SELECT @rule63Id = RuleId FROM [dbo].[ClassificationRules]
WHERE JobTypeId = @jobTypeId AND Pattern = N'EventStatusCode=8' AND IsActive = 1;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ClassificationRules]
               WHERE JobTypeId = @jobTypeId AND Pattern = N'EventStatusCode=9' AND IsActive = 1)
    INSERT INTO [dbo].[ClassificationRules]
        (JobTypeId, ErrorTypeId, Pattern, Confidence, Priority, IsActive)
    VALUES (@jobTypeId, @errorType8Id, N'EventStatusCode=9', 0.90, 1, 1);
SELECT @rule64Id = RuleId FROM [dbo].[ClassificationRules]
WHERE JobTypeId = @jobTypeId AND Pattern = N'EventStatusCode=9' AND IsActive = 1;

IF NOT EXISTS (SELECT 1 FROM [dbo].[ClassificationRules]
               WHERE JobTypeId = @jobTypeId AND Pattern = N'FileStatusCode=22' AND IsActive = 1)
    INSERT INTO [dbo].[ClassificationRules]
        (JobTypeId, ErrorTypeId, Pattern, Confidence, Priority, IsActive)
    VALUES (@jobTypeId, @errorType18Id, N'FileStatusCode=22', 0.90, 1, 1);
SELECT @rule65Id = RuleId FROM [dbo].[ClassificationRules]
WHERE JobTypeId = @jobTypeId AND Pattern = N'FileStatusCode=22' AND IsActive = 1;

-- ── Link classification rules to this job ─────────────────
IF NOT EXISTS (SELECT 1 FROM [dbo].[MonitoredJobRules] WHERE MonitoredJobId = @jobId AND RuleId = @rule63Id)
    INSERT INTO [dbo].[MonitoredJobRules] (MonitoredJobId, RuleId, IsActive) VALUES (@jobId, @rule63Id, 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[MonitoredJobRules] WHERE MonitoredJobId = @jobId AND RuleId = @rule64Id)
    INSERT INTO [dbo].[MonitoredJobRules] (MonitoredJobId, RuleId, IsActive) VALUES (@jobId, @rule64Id, 1);

IF NOT EXISTS (SELECT 1 FROM [dbo].[MonitoredJobRules] WHERE MonitoredJobId = @jobId AND RuleId = @rule65Id)
    INSERT INTO [dbo].[MonitoredJobRules] (MonitoredJobId, RuleId, IsActive) VALUES (@jobId, @rule65Id, 1);

-- ── Fix policy — FailedStatus: auto-heal EventStatusCode → 5 ─────────────────
-- Keyed by (MonitoredJobId, ErrorTypeId) — one enabled override allowed.
IF NOT EXISTS (SELECT 1 FROM [dbo].[FixPolicyRules]
               WHERE MonitoredJobId = @jobId AND ErrorTypeId = @errorType8Id AND Enabled = 1)
    INSERT INTO [dbo].[FixPolicyRules]
        (JobTypeId, ErrorTypeId, MonitoredJobId,
         ActionToApply, FixCategory, IsAutoHealEligible, Enabled,
         CreatedBy, ActionTimestamp,
         ActionType, ActionPayload,
         SuggestedBy, SuggestedFromHash, SuggestedConfidence)
    VALUES
        (@jobTypeId, @errorType8Id, @jobId,
         N'set EventStatusCode=5', N'DbFix', 1, 1,
         N'admin', GETDATE(),
         N'SqlScript',
         N'update dbo.Event set EventStatusCode=5,updateDate=getdate(),updateUser=''MAIA'' WHERE [id] = ''{sourceId}''',
         NULL, NULL, NULL);

-- FailedFeedback (FileStatusCode=22): classified only, no automated fix.
-- Operator resolves manually via Recommendations screen.

COMMIT;

-- Verify
SELECT N'Job' AS Entity, MonitoredJobId AS Id, [Name] FROM [dbo].[MonitoredJobs]  WHERE [Name] = N'Nsg-Events'
UNION ALL
SELECT N'Source', ScanSourceId, [Name] FROM [dbo].[ScanSources] WHERE MonitoredJobId = (SELECT MonitoredJobId FROM [dbo].[MonitoredJobs] WHERE [Name] = N'Nsg-Events')
UNION ALL
SELECT N'ScanRule', CheckRuleId, TargetField + N'=' + ISNULL(ExpectedValue,'?') FROM [dbo].[ScanCheckRules] WHERE ScanSourceId IN (SELECT ScanSourceId FROM [dbo].[ScanSources] WHERE MonitoredJobId = (SELECT MonitoredJobId FROM [dbo].[MonitoredJobs] WHERE [Name] = N'Nsg-Events'))
UNION ALL
SELECT N'ClassRule', RuleId, Pattern FROM [dbo].[ClassificationRules] WHERE RuleId IN (SELECT RuleId FROM [dbo].[MonitoredJobRules] WHERE MonitoredJobId = (SELECT MonitoredJobId FROM [dbo].[MonitoredJobs] WHERE [Name] = N'Nsg-Events'))
UNION ALL
SELECT N'FixPolicy', RuleId, ActionToApply FROM [dbo].[FixPolicyRules] WHERE MonitoredJobId = (SELECT MonitoredJobId FROM [dbo].[MonitoredJobs] WHERE [Name] = N'Nsg-Events');
