-- ============================================================
-- MAIA Job Config Capture
-- Run BEFORE clear-dev-data.sql. Share results to generate seed.
-- ============================================================

DECLARE @jobName NVARCHAR(200) = 'Nsg-Events';
DECLARE @jobId   INT;
SELECT  @jobId = MonitoredJobId
FROM    [dbo].[MonitoredJobs]
WHERE   [Name] = @jobName;

-- ── 1. Job + JobType ───────────────────────────────────────
SELECT  j.*, jt.[Name] AS JobTypeName
FROM    [dbo].[MonitoredJobs] j
JOIN    [dbo].[JobTypes]      jt ON jt.JobTypeId = j.JobTypeId
WHERE   j.MonitoredJobId = @jobId;

-- ── 2. Scan sources ────────────────────────────────────────
SELECT  s.*, st.[Name] AS ScanTypeName
FROM    [dbo].[ScanSources] s
JOIN    [dbo].[ScanTypes]   st ON st.ScanTypeId = s.ScanTypeId
WHERE   s.MonitoredJobId = @jobId;

-- ── 3. Scan check rules ────────────────────────────────────
SELECT  r.*
FROM    [dbo].[ScanCheckRules] r
JOIN    [dbo].[ScanSources]    s ON s.ScanSourceId = r.ScanSourceId
WHERE   s.MonitoredJobId = @jobId;

-- ── 4. Linked classification rules ────────────────────────
SELECT  cr.*, et.Code AS ErrorTypeCode
FROM    [dbo].[ClassificationRules] cr
JOIN    [dbo].[MonitoredJobRules]   mjr ON mjr.RuleId      = cr.RuleId
JOIN    [dbo].[ErrorTypes]          et  ON et.ErrorTypeId  = cr.ErrorTypeId
WHERE   mjr.MonitoredJobId = @jobId;

-- ── 5. Fix policy overrides scoped to this job ─────────────
SELECT  fp.*, et.Code AS ErrorTypeCode
FROM    [dbo].[FixPolicyRules] fp
JOIN    [dbo].[ErrorTypes]     et ON et.ErrorTypeId = fp.ErrorTypeId
WHERE   fp.MonitoredJobId = @jobId;

-- ── 6. Error types used by this job ───────────────────────
SELECT  et.*
FROM    [dbo].[ErrorTypes] et
WHERE   et.ErrorTypeId IN (
    SELECT cr.ErrorTypeId
    FROM   [dbo].[ClassificationRules] cr
    JOIN   [dbo].[MonitoredJobRules]   mjr ON mjr.RuleId = cr.RuleId
    WHERE  mjr.MonitoredJobId = @jobId
    UNION
    SELECT fp.ErrorTypeId
    FROM   [dbo].[FixPolicyRules] fp
    WHERE  fp.MonitoredJobId = @jobId
);
