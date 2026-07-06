
CREATE TABLE [ErrorTypes] (
    [ErrorTypeId] int NOT NULL IDENTITY,
    [Code] nvarchar(50) NOT NULL,
    [DisplayName] nvarchar(100) NOT NULL,
    [Description] nvarchar(500) NULL,
    [Severity] nvarchar(20) NOT NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    CONSTRAINT [PK_ErrorTypes] PRIMARY KEY ([ErrorTypeId])
);
GO

CREATE TABLE [JobTypes] (
    [JobTypeId] int NOT NULL IDENTITY,
    [Name] nvarchar(100) NOT NULL,
    [Description] nvarchar(500) NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    CONSTRAINT [PK_JobTypes] PRIMARY KEY ([JobTypeId])
);
GO

CREATE TABLE [ClassificationRules] (
    [RuleId] int NOT NULL IDENTITY,
    [JobTypeId] int NOT NULL,
    [ErrorTypeId] int NOT NULL,
    [Pattern] nvarchar(500) NOT NULL,
    [Confidence] decimal(5,2) NOT NULL,
    [Priority] int NOT NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedBy] nvarchar(100) NULL,
    CONSTRAINT [PK_ClassificationRules] PRIMARY KEY ([RuleId]),
    CONSTRAINT [FK_ClassificationRules_ErrorTypes_ErrorTypeId] FOREIGN KEY ([ErrorTypeId]) REFERENCES [ErrorTypes] ([ErrorTypeId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_ClassificationRules_JobTypes_JobTypeId] FOREIGN KEY ([JobTypeId]) REFERENCES [JobTypes] ([JobTypeId]) ON DELETE NO ACTION
);
GO

CREATE TABLE [FixPolicyRules] (
    [RuleId] int NOT NULL IDENTITY,
    [JobTypeId] int NOT NULL,
    [ErrorTypeId] int NOT NULL,
    [ActionToApply] nvarchar(300) NOT NULL,
    [FixCategory] nvarchar(50) NOT NULL,
    [IsAutoHealEligible] bit NOT NULL DEFAULT CAST(0 AS bit),
    [Enabled] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedBy] nvarchar(100) NULL,
    [ActionTimestamp] datetime2 NOT NULL DEFAULT (GETDATE()),
    CONSTRAINT [PK_FixPolicyRules] PRIMARY KEY ([RuleId]),
    CONSTRAINT [FK_FixPolicyRules_ErrorTypes_ErrorTypeId] FOREIGN KEY ([ErrorTypeId]) REFERENCES [ErrorTypes] ([ErrorTypeId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_FixPolicyRules_JobTypes_JobTypeId] FOREIGN KEY ([JobTypeId]) REFERENCES [JobTypes] ([JobTypeId]) ON DELETE NO ACTION
);
GO

CREATE TABLE [MonitoredJobs] (
    [MonitoredJobId] int NOT NULL IDENTITY,
    [Name] nvarchar(200) NOT NULL,
    [DisplayName] nvarchar(300) NULL,
    [JobTypeId] int NOT NULL,
    [LogPathTemplate] nvarchar(500) NULL,
    [SourceTable] nvarchar(200) NULL,
    [ConnectionName] nvarchar(200) NULL,
    [PollingIntervalSeconds] int NOT NULL DEFAULT 300,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [Description] nvarchar(1000) NULL,
    [CreatedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    CONSTRAINT [PK_MonitoredJobs] PRIMARY KEY ([MonitoredJobId]),
    CONSTRAINT [FK_MonitoredJobs_JobTypes_JobTypeId] FOREIGN KEY ([JobTypeId]) REFERENCES [JobTypes] ([JobTypeId]) ON DELETE NO ACTION
);
GO

CREATE TABLE [JobFailures] (
    [FailureId] int NOT NULL IDENTITY,
    [JobId] int NOT NULL,
    [JobTypeId] int NOT NULL,
    [ErrorTypeId] int NULL,
    [MonitoredJobId] int NULL,
    [StepName] nvarchar(200) NULL,
    [FileName] nvarchar(300) NULL,
    [ErrorMessage] nvarchar(max) NULL,
    [DetectedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    [SourceLogPath] nvarchar(200) NOT NULL,
    [Status] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_JobFailures] PRIMARY KEY ([FailureId]),
    CONSTRAINT [FK_JobFailures_ErrorTypes_ErrorTypeId] FOREIGN KEY ([ErrorTypeId]) REFERENCES [ErrorTypes] ([ErrorTypeId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_JobFailures_JobTypes_JobTypeId] FOREIGN KEY ([JobTypeId]) REFERENCES [JobTypes] ([JobTypeId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_JobFailures_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE SET NULL
);
GO

CREATE TABLE [MonitoredJobRules] (
    [JobRuleId] int NOT NULL IDENTITY,
    [MonitoredJobId] int NOT NULL,
    [RuleId] int NOT NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    CONSTRAINT [PK_MonitoredJobRules] PRIMARY KEY ([JobRuleId]),
    CONSTRAINT [FK_MonitoredJobRules_ClassificationRules_RuleId] FOREIGN KEY ([RuleId]) REFERENCES [ClassificationRules] ([RuleId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_MonitoredJobRules_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE CASCADE
);
GO

CREATE TABLE [AIRecommendations] (
    [RecommendationId] int NOT NULL IDENTITY,
    [FailureId] int NOT NULL,
    [ErrorTypeId] int NOT NULL,
    [SuggestedAction] nvarchar(500) NOT NULL,
    [FixCategory] nvarchar(50) NOT NULL,
    [ConfidenceScore] decimal(5,2) NOT NULL,
    [Explanation] nvarchar(max) NULL,
    [RecommendedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    [AutoFixAvailable] bit NOT NULL DEFAULT CAST(0 AS bit),
    [OperatorApproved] bit NULL,
    [IsExecuted] bit NOT NULL DEFAULT CAST(0 AS bit),
    CONSTRAINT [PK_AIRecommendations] PRIMARY KEY ([RecommendationId]),
    CONSTRAINT [FK_AIRecommendations_ErrorTypes_ErrorTypeId] FOREIGN KEY ([ErrorTypeId]) REFERENCES [ErrorTypes] ([ErrorTypeId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_AIRecommendations_JobFailures_FailureId] FOREIGN KEY ([FailureId]) REFERENCES [JobFailures] ([FailureId]) ON DELETE CASCADE
);
GO

CREATE TABLE [AuditLog] (
    [AuditId] int NOT NULL IDENTITY,
    [FailureId] int NOT NULL,
    [EventType] nvarchar(100) NOT NULL,
    [Actor] nvarchar(100) NOT NULL,
    [Detail] nvarchar(max) NULL,
    [Timestamp] datetime2 NOT NULL DEFAULT (GETDATE()),
    CONSTRAINT [PK_AuditLog] PRIMARY KEY ([AuditId]),
    CONSTRAINT [FK_AuditLog_JobFailures_FailureId] FOREIGN KEY ([FailureId]) REFERENCES [JobFailures] ([FailureId]) ON DELETE CASCADE
);
GO

CREATE TABLE [FixExecutionLog] (
    [FixId] int NOT NULL IDENTITY,
    [FailureId] int NOT NULL,
    [RecommendationId] int NOT NULL,
    [ExecutedAction] nvarchar(300) NOT NULL,
    [TriggerType] nvarchar(50) NOT NULL,
    [ExecutedBy] nvarchar(100) NOT NULL,
    [ExecutedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    [Success] bit NOT NULL,
    [ResultDetail] nvarchar(max) NULL,
    CONSTRAINT [PK_FixExecutionLog] PRIMARY KEY ([FixId]),
    CONSTRAINT [FK_FixExecutionLog_AIRecommendations_RecommendationId] FOREIGN KEY ([RecommendationId]) REFERENCES [AIRecommendations] ([RecommendationId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_FixExecutionLog_JobFailures_FailureId] FOREIGN KEY ([FailureId]) REFERENCES [JobFailures] ([FailureId]) ON DELETE CASCADE
);
GO

CREATE TABLE [OperatorActions] (
    [ActionId] int NOT NULL IDENTITY,
    [RecommendationId] int NOT NULL,
    [OperatorId] nvarchar(100) NOT NULL,
    [ActionTaken] nvarchar(200) NOT NULL,
    [ActionTimestamp] datetime2 NOT NULL DEFAULT (GETDATE()),
    CONSTRAINT [PK_OperatorActions] PRIMARY KEY ([ActionId]),
    CONSTRAINT [FK_OperatorActions_AIRecommendations_RecommendationId] FOREIGN KEY ([RecommendationId]) REFERENCES [AIRecommendations] ([RecommendationId]) ON DELETE CASCADE
);
GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ErrorTypeId', N'Code', N'Description', N'DisplayName', N'IsActive', N'Severity') AND [object_id] = OBJECT_ID(N'[ErrorTypes]'))
    SET IDENTITY_INSERT [ErrorTypes] ON;
EXEC(N'INSERT INTO [ErrorTypes] ([ErrorTypeId], [Code], [Description], [DisplayName], [IsActive], [Severity])
VALUES (1, N''FileNotFound'', NULL, N''File Not Found'', CAST(1 AS bit), N''High''),
(2, N''DbConnection'', NULL, N''Database Connection Error'', CAST(1 AS bit), N''High''),
(3, N''Timeout'', NULL, N''Execution Timeout'', CAST(1 AS bit), N''Medium''),
(4, N''Transform'', NULL, N''Data Transform Failure'', CAST(1 AS bit), N''Medium''),
(5, N''Permission'', NULL, N''Access Denied'', CAST(1 AS bit), N''High''),
(6, N''Unknown'', NULL, N''Unknown Error'', CAST(1 AS bit), N''Low'')');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ErrorTypeId', N'Code', N'Description', N'DisplayName', N'IsActive', N'Severity') AND [object_id] = OBJECT_ID(N'[ErrorTypes]'))
    SET IDENTITY_INSERT [ErrorTypes] OFF;
GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'JobTypeId', N'Description', N'IsActive', N'Name') AND [object_id] = OBJECT_ID(N'[JobTypes]'))
    SET IDENTITY_INSERT [JobTypes] ON;
EXEC(N'INSERT INTO [JobTypes] ([JobTypeId], [Description], [IsActive], [Name])
VALUES (1, N''SQL Server Integration Services package'', CAST(1 AS bit), N''DTSX''),
(2, N''SQL Server Agent Job'', CAST(1 AS bit), N''SqlAgent''),
(3, N''Python script'', CAST(1 AS bit), N''Python''),
(4, N''PowerShell script'', CAST(1 AS bit), N''PowerShell'')');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'JobTypeId', N'Description', N'IsActive', N'Name') AND [object_id] = OBJECT_ID(N'[JobTypes]'))
    SET IDENTITY_INSERT [JobTypes] OFF;
GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RuleId', N'Confidence', N'CreatedBy', N'ErrorTypeId', N'IsActive', N'JobTypeId', N'Pattern', N'Priority') AND [object_id] = OBJECT_ID(N'[ClassificationRules]'))
    SET IDENTITY_INSERT [ClassificationRules] ON;
EXEC(N'INSERT INTO [ClassificationRules] ([RuleId], [Confidence], [CreatedBy], [ErrorTypeId], [IsActive], [JobTypeId], [Pattern], [Priority])
VALUES (1, 0.95, NULL, 1, CAST(1 AS bit), 1, N''FileNotFoundException'', 1),
(2, 0.85, NULL, 4, CAST(1 AS bit), 1, N''DTS_E_OLEDBERROR'', 2),
(3, 0.93, NULL, 2, CAST(1 AS bit), 1, N''DTS_E_CANNOTACQUIRECONNECTION'', 3),
(4, 0.88, NULL, 3, CAST(1 AS bit), 1, N''Timeout expired'', 4),
(5, 0.95, NULL, 2, CAST(1 AS bit), 2, N''Login failed'', 1),
(6, 0.95, NULL, 1, CAST(1 AS bit), 3, N''FileNotFoundError'', 1),
(7, 0.9, NULL, 2, CAST(1 AS bit), 3, N''ConnectionRefusedError'', 2),
(8, 0.88, NULL, 5, CAST(1 AS bit), 4, N''Access is denied'', 1)');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RuleId', N'Confidence', N'CreatedBy', N'ErrorTypeId', N'IsActive', N'JobTypeId', N'Pattern', N'Priority') AND [object_id] = OBJECT_ID(N'[ClassificationRules]'))
    SET IDENTITY_INSERT [ClassificationRules] OFF;
GO

CREATE INDEX [IX_AIRecommendations_ErrorTypeId] ON [AIRecommendations] ([ErrorTypeId]);
GO

CREATE INDEX [IX_AIRecommendations_FailureId] ON [AIRecommendations] ([FailureId]);
GO

CREATE INDEX [IX_AuditLog_FailureId] ON [AuditLog] ([FailureId]);
GO

CREATE INDEX [IX_ClassificationRules_ErrorTypeId] ON [ClassificationRules] ([ErrorTypeId]);
GO

CREATE INDEX [IX_ClassificationRules_JobTypeId] ON [ClassificationRules] ([JobTypeId]);
GO

CREATE UNIQUE INDEX [IX_ErrorTypes_Code] ON [ErrorTypes] ([Code]);
GO

CREATE INDEX [IX_FixExecutionLog_FailureId] ON [FixExecutionLog] ([FailureId]);
GO

CREATE INDEX [IX_FixExecutionLog_RecommendationId] ON [FixExecutionLog] ([RecommendationId]);
GO

CREATE INDEX [IX_FixPolicyRules_ErrorTypeId] ON [FixPolicyRules] ([ErrorTypeId]);
GO

CREATE INDEX [IX_FixPolicyRules_JobTypeId] ON [FixPolicyRules] ([JobTypeId]);
GO

CREATE INDEX [IX_JobFailures_ErrorTypeId] ON [JobFailures] ([ErrorTypeId]);
GO

CREATE INDEX [IX_JobFailures_JobTypeId] ON [JobFailures] ([JobTypeId]);
GO

CREATE INDEX [IX_JobFailures_MonitoredJobId] ON [JobFailures] ([MonitoredJobId]);
GO

CREATE UNIQUE INDEX [IX_MonitoredJobRules_MonitoredJobId_RuleId] ON [MonitoredJobRules] ([MonitoredJobId], [RuleId]);
GO

CREATE INDEX [IX_MonitoredJobRules_RuleId] ON [MonitoredJobRules] ([RuleId]);
GO

CREATE INDEX [IX_MonitoredJobs_JobTypeId] ON [MonitoredJobs] ([JobTypeId]);
GO

CREATE UNIQUE INDEX [IX_MonitoredJobs_Name] ON [MonitoredJobs] ([Name]);
GO

CREATE INDEX [IX_OperatorActions_RecommendationId] ON [OperatorActions] ([RecommendationId]);
GO

GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'MonitoredJobId', N'ConnectionName', N'CreatedAt', N'Description', N'DisplayName', N'IsActive', N'JobTypeId', N'LogPathTemplate', N'Name', N'PollingIntervalSeconds', N'SourceTable') AND [object_id] = OBJECT_ID(N'[MonitoredJobs]'))
    SET IDENTITY_INSERT [MonitoredJobs] ON;
EXEC(N'INSERT INTO [MonitoredJobs] ([MonitoredJobId], [ConnectionName], [CreatedAt], [Description], [DisplayName], [IsActive], [JobTypeId], [LogPathTemplate], [Name], [PollingIntervalSeconds], [SourceTable])
VALUES (1, NULL, ''2026-04-28T00:00:00.0000000Z'', N''עיבוד ניודים נכנסים'', N''Trap Interfaces'', CAST(1 AS bit), 1, N''c:\logs\Trap*.log'', N''TrapInterfaces'', 300, NULL)');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'MonitoredJobId', N'ConnectionName', N'CreatedAt', N'Description', N'DisplayName', N'IsActive', N'JobTypeId', N'LogPathTemplate', N'Name', N'PollingIntervalSeconds', N'SourceTable') AND [object_id] = OBJECT_ID(N'[MonitoredJobs]'))
    SET IDENTITY_INSERT [MonitoredJobs] OFF;
GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'JobRuleId', N'IsActive', N'MonitoredJobId', N'RuleId') AND [object_id] = OBJECT_ID(N'[MonitoredJobRules]'))
    SET IDENTITY_INSERT [MonitoredJobRules] ON;
EXEC(N'INSERT INTO [MonitoredJobRules] ([JobRuleId], [IsActive], [MonitoredJobId], [RuleId])
VALUES (1, CAST(1 AS bit), 1, 4)');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'JobRuleId', N'IsActive', N'MonitoredJobId', N'RuleId') AND [object_id] = OBJECT_ID(N'[MonitoredJobRules]'))
    SET IDENTITY_INSERT [MonitoredJobRules] OFF;
GO

GO

ALTER TABLE [FixPolicyRules] ADD [ActionPayload] nvarchar(max) NULL;
GO

ALTER TABLE [FixPolicyRules] ADD [ActionType] nvarchar(50) NOT NULL DEFAULT N'Manual';
GO

GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RuleId', N'ActionPayload', N'ActionTimestamp', N'ActionToApply', N'ActionType', N'CreatedBy', N'Enabled', N'ErrorTypeId', N'FixCategory', N'IsAutoHealEligible', N'JobTypeId') AND [object_id] = OBJECT_ID(N'[FixPolicyRules]'))
    SET IDENTITY_INSERT [FixPolicyRules] ON;
EXEC(N'INSERT INTO [FixPolicyRules] ([RuleId], [ActionPayload], [ActionTimestamp], [ActionToApply], [ActionType], [CreatedBy], [Enabled], [ErrorTypeId], [FixCategory], [IsAutoHealEligible], [JobTypeId])
VALUES (1, N''http://jobs.internal/api/jobs/{failureId}/retry'', ''2026-04-28T00:00:00.0000000Z'', N''Retry DTSX job via job-management API'', N''ApiCall'', N''System'', CAST(1 AS bit), 3, N''Retry'', CAST(1 AS bit), 1)');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RuleId', N'ActionPayload', N'ActionTimestamp', N'ActionToApply', N'ActionType', N'CreatedBy', N'Enabled', N'ErrorTypeId', N'FixCategory', N'IsAutoHealEligible', N'JobTypeId') AND [object_id] = OBJECT_ID(N'[FixPolicyRules]'))
    SET IDENTITY_INSERT [FixPolicyRules] OFF;
GO

GO

ALTER TABLE [MonitoredJobs] ADD [CheckColumn] nvarchar(200) NULL;
GO

ALTER TABLE [MonitoredJobs] ADD [LogSourceUrl] nvarchar(500) NULL;
GO

ALTER TABLE [MonitoredJobs] ADD [RangeMax] decimal(18,4) NULL;
GO

ALTER TABLE [MonitoredJobs] ADD [RangeMin] decimal(18,4) NULL;
GO

ALTER TABLE [MonitoredJobs] ADD [ScanType] nvarchar(50) NOT NULL DEFAULT N'FileSystem';
GO

EXEC(N'UPDATE [MonitoredJobs] SET [CheckColumn] = NULL, [LogSourceUrl] = NULL, [RangeMax] = NULL, [RangeMin] = NULL
WHERE [MonitoredJobId] = 1;
SELECT @@ROWCOUNT');
GO

GO

DECLARE @var0 sysname;
SELECT @var0 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'CheckColumn');
IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var0 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [CheckColumn];
GO

DECLARE @var1 sysname;
SELECT @var1 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'RangeMax');
IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var1 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [RangeMax];
GO

DECLARE @var2 sysname;
SELECT @var2 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'RangeMin');
IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var2 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [RangeMin];
GO

DECLARE @var3 sysname;
SELECT @var3 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'ScanType');
IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var3 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [ScanType];
GO

ALTER TABLE [MonitoredJobs] ADD [ScanTypeId] int NOT NULL DEFAULT 1;
GO

CREATE TABLE [ScanCheckRules] (
    [CheckRuleId] int NOT NULL IDENTITY,
    [MonitoredJobId] int NOT NULL,
    [CheckType] nvarchar(50) NOT NULL,
    [TargetField] nvarchar(200) NOT NULL,
    [MinValue] decimal(18,4) NULL,
    [MaxValue] decimal(18,4) NULL,
    [ExpectedValue] nvarchar(500) NULL,
    [Severity] nvarchar(20) NOT NULL,
    [Description] nvarchar(500) NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    CONSTRAINT [PK_ScanCheckRules] PRIMARY KEY ([CheckRuleId]),
    CONSTRAINT [FK_ScanCheckRules_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE CASCADE
);
GO

CREATE TABLE [ScanTypes] (
    [ScanTypeId] int NOT NULL IDENTITY,
    [Name] nvarchar(100) NOT NULL,
    [Description] nvarchar(500) NULL,
    CONSTRAINT [PK_ScanTypes] PRIMARY KEY ([ScanTypeId])
);
GO

EXEC(N'UPDATE [MonitoredJobs] SET [ScanTypeId] = 1
WHERE [MonitoredJobId] = 1;
SELECT @@ROWCOUNT');
GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ScanTypeId', N'Description', N'Name') AND [object_id] = OBJECT_ID(N'[ScanTypes]'))
    SET IDENTITY_INSERT [ScanTypes] ON;
EXEC(N'INSERT INTO [ScanTypes] ([ScanTypeId], [Description], [Name])
VALUES (1, N''Scan log files in a folder matching glob patterns'', N''FileSystem''),
(2, N''Query a SQL table and check column values against rules'', N''Database''),
(3, N''Poll an HTTP endpoint and inspect the response'', N''ApiEndpoint'')');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ScanTypeId', N'Description', N'Name') AND [object_id] = OBJECT_ID(N'[ScanTypes]'))
    SET IDENTITY_INSERT [ScanTypes] OFF;
GO

CREATE INDEX [IX_MonitoredJobs_ScanTypeId] ON [MonitoredJobs] ([ScanTypeId]);
GO

CREATE INDEX [IX_ScanCheckRules_MonitoredJobId] ON [ScanCheckRules] ([MonitoredJobId]);
GO

ALTER TABLE [MonitoredJobs] ADD CONSTRAINT [FK_MonitoredJobs_ScanTypes_ScanTypeId] FOREIGN KEY ([ScanTypeId]) REFERENCES [ScanTypes] ([ScanTypeId]) ON DELETE NO ACTION;
GO

GO

DECLARE @var4 sysname;
SELECT @var4 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'SourceTable');
IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var4 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [SourceTable];
GO

ALTER TABLE [ScanCheckRules] ADD [SourceTable] nvarchar(200) NULL;
GO

GO

CREATE TABLE [ScanFileWatermarks] (
    [WatermarkId] int NOT NULL IDENTITY,
    [MonitoredJobId] int NOT NULL,
    [FilePath] nvarchar(500) NOT NULL,
    [ByteOffset] bigint NOT NULL,
    [LastScannedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    CONSTRAINT [PK_ScanFileWatermarks] PRIMARY KEY ([WatermarkId]),
    CONSTRAINT [FK_ScanFileWatermarks_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE CASCADE
);
GO

CREATE UNIQUE INDEX [IX_ScanFileWatermarks_MonitoredJobId_FilePath] ON [ScanFileWatermarks] ([MonitoredJobId], [FilePath]);
GO

GO

ALTER TABLE [ScanCheckRules] ADD [WatermarkColumn] nvarchar(200) NULL;
GO

CREATE TABLE [ScanDbWatermarks] (
    [WatermarkId] int NOT NULL IDENTITY,
    [CheckRuleId] int NOT NULL,
    [WatermarkValue] nvarchar(100) NOT NULL,
    [LastScannedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    CONSTRAINT [PK_ScanDbWatermarks] PRIMARY KEY ([WatermarkId]),
    CONSTRAINT [FK_ScanDbWatermarks_ScanCheckRules_CheckRuleId] FOREIGN KEY ([CheckRuleId]) REFERENCES [ScanCheckRules] ([CheckRuleId]) ON DELETE CASCADE
);
GO

CREATE UNIQUE INDEX [IX_ScanDbWatermarks_CheckRuleId] ON [ScanDbWatermarks] ([CheckRuleId]);
GO

GO

DECLARE @var5 sysname;
SELECT @var5 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[JobFailures]') AND [c].[name] = N'FileName');
IF @var5 IS NOT NULL EXEC(N'ALTER TABLE [JobFailures] DROP CONSTRAINT [' + @var5 + '];');
ALTER TABLE [JobFailures] DROP COLUMN [FileName];
GO

ALTER TABLE [JobFailures] ADD [SourceId] nvarchar(500) NULL;
GO

GO

ALTER TABLE [ScanCheckRules] ADD [SourceIdColumn] nvarchar(200) NULL;
GO

GO

ALTER TABLE [ScanTypes] ADD [LeaseDurationSeconds] int NOT NULL DEFAULT 300;
GO

CREATE TABLE [MonitoredJobLeases] (
    [MonitoredJobId] int NOT NULL,
    [LeasedBy] nvarchar(200) NULL,
    [LeasedAt] datetime2(3) NULL,
    [LeasedUntil] datetime2(3) NULL,
    [NextEligibleAt] datetime2(3) NOT NULL DEFAULT ('0001-01-01'),
    [LastRunStartedAt] datetime2(3) NULL,
    [LastRunCompletedAt] datetime2(3) NULL,
    [LastRunOutcome] nvarchar(50) NULL,
    [LastRunError] nvarchar(2000) NULL,
    CONSTRAINT [PK_MonitoredJobLeases] PRIMARY KEY ([MonitoredJobId]),
    CONSTRAINT [FK_MonitoredJobLeases_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE CASCADE
);
GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'MonitoredJobId', N'LastRunCompletedAt', N'LastRunError', N'LastRunOutcome', N'LastRunStartedAt', N'LeasedAt', N'LeasedBy', N'LeasedUntil') AND [object_id] = OBJECT_ID(N'[MonitoredJobLeases]'))
    SET IDENTITY_INSERT [MonitoredJobLeases] ON;
EXEC(N'INSERT INTO [MonitoredJobLeases] ([MonitoredJobId], [LastRunCompletedAt], [LastRunError], [LastRunOutcome], [LastRunStartedAt], [LeasedAt], [LeasedBy], [LeasedUntil])
VALUES (1, NULL, NULL, NULL, NULL, NULL, NULL, NULL)');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'MonitoredJobId', N'LastRunCompletedAt', N'LastRunError', N'LastRunOutcome', N'LastRunStartedAt', N'LeasedAt', N'LeasedBy', N'LeasedUntil') AND [object_id] = OBJECT_ID(N'[MonitoredJobLeases]'))
    SET IDENTITY_INSERT [MonitoredJobLeases] OFF;
GO

EXEC(N'UPDATE [ScanTypes] SET [LeaseDurationSeconds] = 300
WHERE [ScanTypeId] = 1;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ScanTypes] SET [LeaseDurationSeconds] = 1800
WHERE [ScanTypeId] = 2;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ScanTypes] SET [LeaseDurationSeconds] = 60
WHERE [ScanTypeId] = 3;
SELECT @@ROWCOUNT');
GO

CREATE INDEX [IX_MonitoredJobLeases_Eligible] ON [MonitoredJobLeases] ([NextEligibleAt], [LeasedUntil]);
GO

INSERT INTO dbo.MonitoredJobLeases (MonitoredJobId, NextEligibleAt)
SELECT m.MonitoredJobId, '0001-01-01'
FROM dbo.MonitoredJobs m
LEFT JOIN dbo.MonitoredJobLeases l ON l.MonitoredJobId = m.MonitoredJobId
WHERE l.MonitoredJobId IS NULL;
GO

GO

CREATE TABLE [ScanRunHistory] (
    [ScanRunId] int NOT NULL IDENTITY,
    [MonitoredJobId] int NOT NULL,
    [LeasedBy] nvarchar(200) NOT NULL,
    [StartedAt] datetime2(3) NOT NULL,
    [CompletedAt] datetime2(3) NOT NULL,
    [DurationMs] int NOT NULL,
    [Outcome] nvarchar(50) NOT NULL,
    [Error] nvarchar(2000) NULL,
    [FailuresDetected] int NOT NULL,
    [Classifications] int NOT NULL,
    [Recommendations] int NOT NULL,
    CONSTRAINT [PK_ScanRunHistory] PRIMARY KEY ([ScanRunId]),
    CONSTRAINT [FK_ScanRunHistory_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE CASCADE
);
GO

EXEC(N'CREATE INDEX [IX_ScanRunHistory_Failures] ON [ScanRunHistory] ([StartedAt] DESC) INCLUDE ([MonitoredJobId], [Outcome], [Error]) WHERE [Outcome] <> ''Success''');
GO

CREATE INDEX [IX_ScanRunHistory_Job_StartedAt] ON [ScanRunHistory] ([MonitoredJobId], [StartedAt] DESC) INCLUDE ([CompletedAt], [DurationMs], [Outcome], [FailuresDetected], [Classifications], [Recommendations]);
GO

GO

DELETE FROM [AuditLog];
GO

DECLARE @var6 sysname;
SELECT @var6 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[AuditLog]') AND [c].[name] = N'FailureId');
IF @var6 IS NOT NULL EXEC(N'ALTER TABLE [AuditLog] DROP CONSTRAINT [' + @var6 + '];');
ALTER TABLE [AuditLog] ALTER COLUMN [FailureId] int NULL;
GO

ALTER TABLE [AuditLog] ADD [EntityId] nvarchar(100) NULL;
GO

ALTER TABLE [AuditLog] ADD [EntityType] nvarchar(100) NULL;
GO

GO

DROP INDEX [IX_FixPolicyRules_JobTypeId] ON [FixPolicyRules];
GO

EXEC(N'CREATE UNIQUE INDEX [UX_FixPolicyRules_ActiveKey] ON [FixPolicyRules] ([JobTypeId], [ErrorTypeId]) WHERE [Enabled] = 1');
GO

GO

DROP INDEX [UX_FixPolicyRules_ActiveKey] ON [FixPolicyRules];
GO

ALTER TABLE [FixPolicyRules] ADD [MonitoredJobId] int NULL;
GO

EXEC(N'UPDATE [FixPolicyRules] SET [MonitoredJobId] = NULL
WHERE [RuleId] = 1;
SELECT @@ROWCOUNT');
GO

EXEC(N'CREATE UNIQUE INDEX [UX_FixPolicyRules_DefaultActiveKey] ON [FixPolicyRules] ([JobTypeId], [ErrorTypeId]) WHERE [Enabled] = 1 AND [MonitoredJobId] IS NULL');
GO

EXEC(N'CREATE UNIQUE INDEX [UX_FixPolicyRules_OverrideActiveKey] ON [FixPolicyRules] ([MonitoredJobId], [ErrorTypeId]) WHERE [Enabled] = 1 AND [MonitoredJobId] IS NOT NULL');
GO

ALTER TABLE [FixPolicyRules] ADD CONSTRAINT [FK_FixPolicyRules_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE NO ACTION;
GO

GO

ALTER TABLE [ScanCheckRules] ADD [FilePathColumn] nvarchar(100) NULL;
GO

ALTER TABLE [ScanCheckRules] ADD [InputPathPattern] nvarchar(500) NULL;
GO

ALTER TABLE [MonitoredJobs] ADD [InputFolder] nvarchar(500) NULL;
GO

ALTER TABLE [JobFailures] ADD [SourceFilePath] nvarchar(500) NULL;
GO

CREATE TABLE [FixPolicyRuleSteps] (
    [StepId] int NOT NULL IDENTITY,
    [RuleId] int NOT NULL,
    [StepOrder] int NOT NULL,
    [ActionType] nvarchar(50) NOT NULL,
    [ActionPayload] nvarchar(max) NOT NULL,
    [Description] nvarchar(200) NULL,
    CONSTRAINT [PK_FixPolicyRuleSteps] PRIMARY KEY ([StepId]),
    CONSTRAINT [FK_FixPolicyRuleSteps_FixPolicyRules_RuleId] FOREIGN KEY ([RuleId]) REFERENCES [FixPolicyRules] ([RuleId]) ON DELETE CASCADE
);
GO

EXEC(N'UPDATE [MonitoredJobs] SET [InputFolder] = NULL
WHERE [MonitoredJobId] = 1;
SELECT @@ROWCOUNT');
GO

CREATE UNIQUE INDEX [UX_FixPolicyRuleSteps_RuleId_StepOrder] ON [FixPolicyRuleSteps] ([RuleId], [StepOrder]);
GO

GO

ALTER TABLE [AIRecommendations] ADD [ClaimedAt] datetime2 NULL;
GO

ALTER TABLE [AIRecommendations] ADD [ClaimedBy] nvarchar(200) NULL;
GO

EXEC(N'CREATE INDEX [IX_AIRecommendations_ClaimEligible] ON [AIRecommendations] ([IsExecuted], [ClaimedAt]) WHERE [IsExecuted] = 0');
GO

GO

ALTER TABLE [FixPolicyRules] ADD [SuggestedBy] nvarchar(50) NULL;
GO

ALTER TABLE [FixPolicyRules] ADD [SuggestedConfidence] decimal(5,2) NULL;
GO

ALTER TABLE [FixPolicyRules] ADD [SuggestedFromHash] nvarchar(64) NULL;
GO

ALTER TABLE [ClassificationRules] ADD [SuggestedBy] nvarchar(50) NULL;
GO

ALTER TABLE [ClassificationRules] ADD [SuggestedConfidence] decimal(5,2) NULL;
GO

ALTER TABLE [ClassificationRules] ADD [SuggestedFromHash] nvarchar(64) NULL;
GO

EXEC(N'UPDATE [ClassificationRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 1;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ClassificationRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 2;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ClassificationRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 3;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ClassificationRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 4;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ClassificationRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 5;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ClassificationRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 6;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ClassificationRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 7;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [ClassificationRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 8;
SELECT @@ROWCOUNT');
GO

EXEC(N'UPDATE [FixPolicyRules] SET [SuggestedBy] = NULL, [SuggestedConfidence] = NULL, [SuggestedFromHash] = NULL
WHERE [RuleId] = 1;
SELECT @@ROWCOUNT');
GO

GO

DROP INDEX [IX_ClassificationRules_JobTypeId] ON [ClassificationRules];
GO

EXEC(N'CREATE UNIQUE INDEX [UX_ClassificationRules_ActiveKey] ON [ClassificationRules] ([JobTypeId], [Pattern]) WHERE [IsActive] = 1');
GO

GO

ALTER TABLE [ScanRunHistory] ADD [IdentifierExtractionFailures] int NOT NULL DEFAULT 0;
GO

ALTER TABLE [ScanRunHistory] ADD [OversizeFileSkips] int NOT NULL DEFAULT 0;
GO

ALTER TABLE [ScanCheckRules] ADD [ExtractorLocator] nvarchar(500) NULL;
GO

ALTER TABLE [ScanCheckRules] ADD [ExtractorPredicateType] nvarchar(50) NULL;
GO

ALTER TABLE [ScanCheckRules] ADD [ExtractorPredicateValue] nvarchar(500) NULL;
GO

ALTER TABLE [ScanCheckRules] ADD [ExtractorType] nvarchar(50) NULL;
GO

ALTER TABLE [ScanCheckRules] ADD [IdentifierLocator] nvarchar(500) NULL;
GO

ALTER TABLE [MonitoredJobs] ADD [IncludeSubfolders] bit NOT NULL DEFAULT CAST(0 AS bit);
GO

CREATE TABLE [ScanContentWatermarks] (
    [WatermarkId] int NOT NULL IDENTITY,
    [MonitoredJobId] int NOT NULL,
    [FilePath] nvarchar(500) NOT NULL,
    [LastScannedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    [LastModifiedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_ScanContentWatermarks] PRIMARY KEY ([WatermarkId]),
    CONSTRAINT [FK_ScanContentWatermarks_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE CASCADE
);
GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ScanTypeId', N'Description', N'LeaseDurationSeconds', N'Name') AND [object_id] = OBJECT_ID(N'[ScanTypes]'))
    SET IDENTITY_INSERT [ScanTypes] ON;
EXEC(N'INSERT INTO [ScanTypes] ([ScanTypeId], [Description], [LeaseDurationSeconds], [Name])
VALUES (4, N''Structured extraction from input data files (XML, …)'', 300, N''FileContent'')');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ScanTypeId', N'Description', N'LeaseDurationSeconds', N'Name') AND [object_id] = OBJECT_ID(N'[ScanTypes]'))
    SET IDENTITY_INSERT [ScanTypes] OFF;
GO

CREATE UNIQUE INDEX [IX_ScanContentWatermarks_MonitoredJobId_FilePath] ON [ScanContentWatermarks] ([MonitoredJobId], [FilePath]);
GO

GO

ALTER TABLE [ScanRunHistory] ADD [PredicateUnevaluableSkips] int NOT NULL DEFAULT 0;
GO

GO

ALTER TABLE [ScanRunHistory] ADD [ScanSourceId] int NULL;
GO

ALTER TABLE [ScanFileWatermarks] ADD [ScanSourceId] int NULL;
GO

ALTER TABLE [ScanContentWatermarks] ADD [ScanSourceId] int NULL;
GO

ALTER TABLE [ScanCheckRules] ADD [ScanSourceId] int NULL;
GO

ALTER TABLE [JobFailures] ADD [ScanSourceId] int NULL;
GO

CREATE TABLE [ScanSources] (
    [ScanSourceId] int NOT NULL IDENTITY,
    [MonitoredJobId] int NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [ScanTypeId] int NOT NULL,
    [LogFolder] nvarchar(500) NULL,
    [SearchPatterns] nvarchar(500) NULL,
    [InputFolder] nvarchar(500) NULL,
    [IncludeSubfolders] bit NOT NULL DEFAULT CAST(0 AS bit),
    [ConnectionName] nvarchar(200) NULL,
    [LogSourceUrl] nvarchar(500) NULL,
    [PollingIntervalSeconds] int NOT NULL DEFAULT 300,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    CONSTRAINT [PK_ScanSources] PRIMARY KEY ([ScanSourceId]),
    CONSTRAINT [FK_ScanSources_MonitoredJobs_MonitoredJobId] FOREIGN KEY ([MonitoredJobId]) REFERENCES [MonitoredJobs] ([MonitoredJobId]) ON DELETE CASCADE,
    CONSTRAINT [FK_ScanSources_ScanTypes_ScanTypeId] FOREIGN KEY ([ScanTypeId]) REFERENCES [ScanTypes] ([ScanTypeId]) ON DELETE NO ACTION
);
GO

CREATE INDEX [IX_ScanRunHistory_Source_StartedAt] ON [ScanRunHistory] ([ScanSourceId], [StartedAt] DESC);
GO

CREATE INDEX [IX_ScanFileWatermarks_ScanSourceId] ON [ScanFileWatermarks] ([ScanSourceId]);
GO

CREATE INDEX [IX_ScanContentWatermarks_ScanSourceId] ON [ScanContentWatermarks] ([ScanSourceId]);
GO

CREATE INDEX [IX_ScanCheckRules_ScanSourceId] ON [ScanCheckRules] ([ScanSourceId]);
GO

CREATE INDEX [IX_JobFailures_ScanSourceId] ON [JobFailures] ([ScanSourceId]);
GO

CREATE INDEX [IX_ScanSources_MonitoredJobId] ON [ScanSources] ([MonitoredJobId]);
GO

CREATE INDEX [IX_ScanSources_ScanTypeId] ON [ScanSources] ([ScanTypeId]);
GO

ALTER TABLE [JobFailures] ADD CONSTRAINT [FK_JobFailures_ScanSources_ScanSourceId] FOREIGN KEY ([ScanSourceId]) REFERENCES [ScanSources] ([ScanSourceId]);
GO

ALTER TABLE [ScanCheckRules] ADD CONSTRAINT [FK_ScanCheckRules_ScanSources_ScanSourceId] FOREIGN KEY ([ScanSourceId]) REFERENCES [ScanSources] ([ScanSourceId]);
GO

ALTER TABLE [ScanContentWatermarks] ADD CONSTRAINT [FK_ScanContentWatermarks_ScanSources_ScanSourceId] FOREIGN KEY ([ScanSourceId]) REFERENCES [ScanSources] ([ScanSourceId]);
GO

ALTER TABLE [ScanFileWatermarks] ADD CONSTRAINT [FK_ScanFileWatermarks_ScanSources_ScanSourceId] FOREIGN KEY ([ScanSourceId]) REFERENCES [ScanSources] ([ScanSourceId]);
GO

ALTER TABLE [ScanRunHistory] ADD CONSTRAINT [FK_ScanRunHistory_ScanSources_ScanSourceId] FOREIGN KEY ([ScanSourceId]) REFERENCES [ScanSources] ([ScanSourceId]);
GO

INSERT INTO ScanSources
    (MonitoredJobId, Name, ScanTypeId, LogFolder, SearchPatterns, InputFolder,
     IncludeSubfolders, ConnectionName, LogSourceUrl, PollingIntervalSeconds, IsActive)
SELECT
    j.MonitoredJobId, st.Name, j.ScanTypeId, j.LogFolder, j.SearchPatterns, j.InputFolder,
    j.IncludeSubfolders, j.ConnectionName, j.LogSourceUrl, j.PollingIntervalSeconds, j.IsActive
FROM MonitoredJobs j
JOIN ScanTypes st ON st.ScanTypeId = j.ScanTypeId;
GO

UPDATE r SET r.ScanSourceId = s.ScanSourceId
FROM ScanCheckRules r JOIN ScanSources s ON s.MonitoredJobId = r.MonitoredJobId;
GO

UPDATE w SET w.ScanSourceId = s.ScanSourceId
FROM ScanFileWatermarks w JOIN ScanSources s ON s.MonitoredJobId = w.MonitoredJobId;
GO

UPDATE w SET w.ScanSourceId = s.ScanSourceId
FROM ScanContentWatermarks w JOIN ScanSources s ON s.MonitoredJobId = w.MonitoredJobId;
GO

UPDATE h SET h.ScanSourceId = s.ScanSourceId
FROM ScanRunHistory h JOIN ScanSources s ON s.MonitoredJobId = h.MonitoredJobId;
GO

UPDATE f SET f.ScanSourceId = s.ScanSourceId
FROM JobFailures f JOIN ScanSources s ON s.MonitoredJobId = f.MonitoredJobId;
GO

GO

DECLARE @var7 sysname;
SELECT @var7 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[ScanCheckRules]') AND [c].[name] = N'SourceTable');
IF @var7 IS NOT NULL EXEC(N'ALTER TABLE [ScanCheckRules] DROP CONSTRAINT [' + @var7 + '];');
ALTER TABLE [ScanCheckRules] ALTER COLUMN [SourceTable] nvarchar(max) NULL;
GO

GO

                DELETE FROM [dbo].[ScanContentWatermarks] WHERE [ScanSourceId] IS NULL;
                DELETE FROM [dbo].[ScanFileWatermarks]    WHERE [ScanSourceId] IS NULL;
                DELETE FROM [dbo].[ScanRunHistory]        WHERE [ScanSourceId] IS NULL;
GO

                DROP INDEX [IX_ScanCheckRules_ScanSourceId]        ON [dbo].[ScanCheckRules];
                DROP INDEX [IX_ScanContentWatermarks_ScanSourceId] ON [dbo].[ScanContentWatermarks];
                DROP INDEX [IX_ScanFileWatermarks_ScanSourceId]    ON [dbo].[ScanFileWatermarks];
                DROP INDEX [IX_JobFailures_ScanSourceId]           ON [dbo].[JobFailures];
                DROP INDEX [IX_ScanRunHistory_Source_StartedAt]    ON [dbo].[ScanRunHistory];
                ALTER TABLE [dbo].[ScanCheckRules]        ALTER COLUMN [ScanSourceId] INT NOT NULL;
                ALTER TABLE [dbo].[ScanContentWatermarks] ALTER COLUMN [ScanSourceId] INT NOT NULL;
                ALTER TABLE [dbo].[ScanFileWatermarks]    ALTER COLUMN [ScanSourceId] INT NOT NULL;
                ALTER TABLE [dbo].[JobFailures]           ALTER COLUMN [ScanSourceId] INT NOT NULL;
                ALTER TABLE [dbo].[ScanRunHistory]        ALTER COLUMN [ScanSourceId] INT NOT NULL;
                CREATE INDEX [IX_ScanCheckRules_ScanSourceId]        ON [dbo].[ScanCheckRules]        ([ScanSourceId]);
                CREATE INDEX [IX_ScanContentWatermarks_ScanSourceId] ON [dbo].[ScanContentWatermarks] ([ScanSourceId]);
                CREATE INDEX [IX_ScanFileWatermarks_ScanSourceId]    ON [dbo].[ScanFileWatermarks]    ([ScanSourceId]);
                CREATE INDEX [IX_JobFailures_ScanSourceId]           ON [dbo].[JobFailures]           ([ScanSourceId]);
                CREATE INDEX [IX_ScanRunHistory_Source_StartedAt]    ON [dbo].[ScanRunHistory]        ([ScanSourceId], [StartedAt]);
GO

ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [FK_MonitoredJobs_ScanTypes_ScanTypeId];
GO

DROP INDEX [IX_MonitoredJobs_ScanTypeId] ON [MonitoredJobs];
GO

DECLARE @var8 sysname;
SELECT @var8 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'ConnectionName');
IF @var8 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var8 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [ConnectionName];
GO

DECLARE @var9 sysname;
SELECT @var9 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'IncludeSubfolders');
IF @var9 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var9 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [IncludeSubfolders];
GO

DECLARE @var10 sysname;
SELECT @var10 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'InputFolder');
IF @var10 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var10 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [InputFolder];
GO

DECLARE @var11 sysname;
SELECT @var11 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'LogFolder');
IF @var11 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var11 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [LogFolder];
GO

DECLARE @var12 sysname;
SELECT @var12 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'LogSourceUrl');
IF @var12 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var12 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [LogSourceUrl];
GO

DECLARE @var13 sysname;
SELECT @var13 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'ScanTypeId');
IF @var13 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var13 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [ScanTypeId];
GO

DECLARE @var14 sysname;
SELECT @var14 = [d].[name]
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[MonitoredJobs]') AND [c].[name] = N'SearchPatterns');
IF @var14 IS NOT NULL EXEC(N'ALTER TABLE [MonitoredJobs] DROP CONSTRAINT [' + @var14 + '];');
ALTER TABLE [MonitoredJobs] DROP COLUMN [SearchPatterns];
GO

GO

CREATE TABLE [Roles] (
    [RoleId] int NOT NULL,
    [Name] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_Roles] PRIMARY KEY ([RoleId])
);
GO

CREATE TABLE [Users] (
    [UserId] int NOT NULL IDENTITY,
    [Username] nvarchar(100) NOT NULL,
    [PasswordHash] nvarchar(500) NOT NULL,
    [RoleId] int NOT NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [MustChangePassword] bit NOT NULL DEFAULT CAST(0 AS bit),
    [CreatedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    [LastLoginAt] datetime2 NULL,
    CONSTRAINT [PK_Users] PRIMARY KEY ([UserId]),
    CONSTRAINT [FK_Users_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([RoleId]) ON DELETE NO ACTION
);
GO

CREATE TABLE [UserSessions] (
    [SessionId] int NOT NULL IDENTITY,
    [Token] nvarchar(200) NOT NULL,
    [UserId] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    [LastActivityAt] datetime2 NOT NULL DEFAULT (GETDATE()),
    CONSTRAINT [PK_UserSessions] PRIMARY KEY ([SessionId]),
    CONSTRAINT [FK_UserSessions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserId]) ON DELETE CASCADE
);
GO

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RoleId', N'Name') AND [object_id] = OBJECT_ID(N'[Roles]'))
    SET IDENTITY_INSERT [Roles] ON;
EXEC(N'INSERT INTO [Roles] ([RoleId], [Name])
VALUES (1, N''User''),
(2, N''Operator''),
(3, N''Administrator'')');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RoleId', N'Name') AND [object_id] = OBJECT_ID(N'[Roles]'))
    SET IDENTITY_INSERT [Roles] OFF;
GO

CREATE UNIQUE INDEX [IX_Roles_Name] ON [Roles] ([Name]);
GO

CREATE INDEX [IX_Users_RoleId] ON [Users] ([RoleId]);
GO

CREATE UNIQUE INDEX [IX_Users_Username] ON [Users] ([Username]);
GO

CREATE UNIQUE INDEX [IX_UserSessions_Token] ON [UserSessions] ([Token]);
GO

CREATE INDEX [IX_UserSessions_UserId] ON [UserSessions] ([UserId]);
GO

                IF NOT EXISTS (SELECT 1 FROM [dbo].[Users] WHERE [Username] = N'admin')
                INSERT INTO [dbo].[Users]
                    ([Username], [PasswordHash], [RoleId], [IsActive], [MustChangePassword], [CreatedAt])
                VALUES
                    (N'admin',
                     N'AQAAAAIAAYagAAAAEPnp2IHUgzfCWpWZjNEACdO0lM/CvWnIaW0l8KlxiWw58i93pgNCH9Hu1YAYpn+fpg==',
                     3, 1, 1, GETDATE());
GO

GO

-- TrapInterfaces job (MonitoredJobId = 1) and its dependent rows.
-- FK order: watermarks first (each table's FK column differs), then config rows.
-- ScanDbWatermarks: FK is on CheckRuleId -> ScanCheckRules (no MonitoredJobId column)
DELETE FROM [dbo].[ScanDbWatermarks]
    WHERE [CheckRuleId] IN (SELECT [CheckRuleId] FROM [dbo].[ScanCheckRules] WHERE [MonitoredJobId] = 1);
-- ScanFileWatermarks / ScanContentWatermarks: have MonitoredJobId column
DELETE FROM [dbo].[ScanFileWatermarks]    WHERE [MonitoredJobId] = 1;
DELETE FROM [dbo].[ScanContentWatermarks] WHERE [MonitoredJobId] = 1;
-- ScanRunHistory: FK is on ScanSourceId
DELETE FROM [dbo].[ScanRunHistory]
    WHERE [ScanSourceId] IN (SELECT [ScanSourceId] FROM [dbo].[ScanSources] WHERE [MonitoredJobId] = 1);
-- Config rows (ScanCheckRules before ScanSources; FixPolicyRules/JobRules/Leases before MonitoredJobs)
DELETE FROM [dbo].[ScanCheckRules]     WHERE [MonitoredJobId] = 1;
DELETE FROM [dbo].[ScanSources]        WHERE [MonitoredJobId] = 1;
DELETE FROM [dbo].[FixPolicyRuleSteps] WHERE [RuleId] IN (SELECT [RuleId] FROM [dbo].[FixPolicyRules] WHERE [MonitoredJobId] = 1);
DELETE FROM [dbo].[FixPolicyRules]     WHERE [MonitoredJobId] = 1;
DELETE FROM [dbo].[MonitoredJobRules]  WHERE [MonitoredJobId] = 1;
DELETE FROM [dbo].[MonitoredJobLeases] WHERE [MonitoredJobId] = 1;
DELETE FROM [dbo].[MonitoredJobs]      WHERE [MonitoredJobId] = 1;
-- Dev-seed global fix policy (RuleId = 1: DTSX/ApiCall retry, MonitoredJobId IS NULL)
DELETE FROM [dbo].[FixPolicyRuleSteps] WHERE [RuleId] = 1;
DELETE FROM [dbo].[FixPolicyRules]     WHERE [RuleId] = 1;
-- Dev-seed classification rules (RuleIds 1-8: generic DTSX/SqlAgent/Python/PowerShell patterns)
DELETE FROM [dbo].[ClassificationRules] WHERE [RuleId] IN (1, 2, 3, 4, 5, 6, 7, 8);
GO

IF NOT EXISTS (SELECT 1 FROM [dbo].[JobTypes] WHERE [Name] = N'Exe')
    INSERT INTO [dbo].[JobTypes] ([Name], [Description], [IsActive])
    VALUES (N'Exe', N'Native executable / batch process', 1);
GO

GO