SET QUOTED_IDENTIFIER ON;   -- required to UPDATE tables carrying filtered indexes (FixPolicyRules)
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRAN;

-- ── Issue 2a: add an "Exception" keyword scan rule for job 1003 ───────────────
-- (keep the existing "Error" rule #7; their log lines use "Exception : ...").
IF NOT EXISTS (SELECT 1 FROM ScanCheckRules
               WHERE MonitoredJobId = 1003 AND CheckType = 'ErrorKeyword' AND TargetField = 'Exception')
    INSERT INTO ScanCheckRules (MonitoredJobId, CheckType, TargetField, Severity, IsActive, Description)
    VALUES (1003, 'ErrorKeyword', 'Exception', 'High', 1, 'Detect log lines containing "Exception"');

-- ── Issue 2b: reset the watermark so the already-consumed lines re-scan ───────
DELETE FROM ScanFileWatermarks
WHERE MonitoredJobId = 1003 AND FilePath LIKE '%app-20260601.log';

-- ── Issue 1: split ProcessingError symptoms into dedicated ErrorTypes ─────────
DECLARE @dupKey INT, @nullRef INT;

IF EXISTS (SELECT 1 FROM ErrorTypes WHERE Code = 'DuplicateKey')
    SELECT @dupKey = ErrorTypeId FROM ErrorTypes WHERE Code = 'DuplicateKey';
ELSE
BEGIN
    INSERT INTO ErrorTypes (Code, DisplayName, Description, Severity, IsActive)
    VALUES ('DuplicateKey', 'Duplicate Key', 'Cannot insert duplicate key', 'High', 1);
    SET @dupKey = SCOPE_IDENTITY();
END

IF EXISTS (SELECT 1 FROM ErrorTypes WHERE Code = 'NullReference')
    SELECT @nullRef = ErrorTypeId FROM ErrorTypes WHERE Code = 'NullReference';
ELSE
BEGIN
    INSERT INTO ErrorTypes (Code, DisplayName, Description, Severity, IsActive)
    VALUES ('NullReference', 'Null Reference', 'Object null reference', 'High', 1);
    SET @nullRef = SCOPE_IDENTITY();
END

-- Repoint the two classification rules to their own ErrorTypes
UPDATE ClassificationRules SET ErrorTypeId = @dupKey  WHERE RuleId = 14;  -- "Cannot insert duplicate key"
UPDATE ClassificationRules SET ErrorTypeId = @nullRef WHERE RuleId = 21;  -- "object null referance"

-- Repoint the two fixes (2015 = job-1003 override, 1009 = JobType-1 default)
UPDATE FixPolicyRules SET ErrorTypeId = @dupKey  WHERE RuleId = 2015;  -- override -> DuplicateKey
UPDATE FixPolicyRules SET ErrorTypeId = @nullRef WHERE RuleId = 1009;  -- default  -> NullReference

COMMIT;
SELECT @dupKey AS DuplicateKeyId, @nullRef AS NullReferenceId;
