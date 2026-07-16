-- ============================================================
-- MAIA Development Data Cleanup
-- Deletes all operator-configured / test data.
-- Preserves metadata: JobTypes, ScanTypes, Roles, Users.
-- ============================================================
-- Run in dev only. Order matters — respects all FK constraints.
-- ============================================================

BEGIN TRANSACTION;

-- ── Fix execution history ──────────────────────────────────
DELETE FROM [dbo].[FixExecutionLogs];
DELETE FROM [dbo].[OperatorActions];

-- ── Audit trail ────────────────────────────────────────────
DELETE FROM [dbo].[AuditLog];

-- ── Recommendations (before failures) ─────────────────────
DELETE FROM [dbo].[AIRecommendations];

-- ── Failures ───────────────────────────────────────────────
DELETE FROM [dbo].[JobFailures];

-- ── Scan run history + watermarks (reference ScanSources) ─
DELETE FROM [dbo].[ScanRunHistory];
DELETE FROM [dbo].[ScanFileWatermarks];
DELETE FROM [dbo].[ScanDbWatermarks];
DELETE FROM [dbo].[ScanContentWatermarks];

-- ── Auth sessions (keeps Roles + Users) ───────────────────
DELETE FROM [dbo].[UserSessions];

-- ── Classification rules + job links ──────────────────────
DELETE FROM [dbo].[MonitoredJobRules];
DELETE FROM [dbo].[ClassificationRules];

-- ── Scan rules + sources (before MonitoredJobs) ───────────
DELETE FROM [dbo].[ScanCheckRules];
DELETE FROM [dbo].[ScanSources];

-- ── Leases (CASCADE from MonitoredJobs but explicit here) ─
DELETE FROM [dbo].[MonitoredJobLeases];

-- ── Fix policies (FixPolicyRuleSteps cascade automatically) ─
DELETE FROM [dbo].[FixPolicyRules];    -- steps cascade via ON DELETE CASCADE

-- ── Monitored jobs ─────────────────────────────────────────
DELETE FROM [dbo].[MonitoredJobs];

-- ── Error types (user-configured lookup) ──────────────────
-- Comment out if you want to keep your error type definitions.
DELETE FROM [dbo].[ErrorTypes];

COMMIT;

-- Optionally reset identity sequences so IDs start from 1 again:
 DBCC CHECKIDENT ('[dbo].[MonitoredJobs]',    RESEED, 0);
 DBCC CHECKIDENT ('[dbo].[ScanSources]',       RESEED, 0);
 DBCC CHECKIDENT ('[dbo].[ScanCheckRules]',    RESEED, 0);
 DBCC CHECKIDENT ('[dbo].[ClassificationRules]',RESEED, 0);
 DBCC CHECKIDENT ('[dbo].[FixPolicyRules]',    RESEED, 0);
 DBCC CHECKIDENT ('[dbo].[JobFailures]',        RESEED, 0);
 DBCC CHECKIDENT ('[dbo].[AIRecommendations]',  RESEED, 0);
 DBCC CHECKIDENT ('[dbo].[AuditLog]',           RESEED, 0);
