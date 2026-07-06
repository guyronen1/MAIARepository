using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CleanupDevSeedsAddExeJobType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove dev/test seed data that was baked into early migrations via HasData.
            // Guards (IF EXISTS / WHERE) make this safe on any DB state.

            // FK order: watermarks -> ScanCheckRules -> ScanSources
            //           MonitoredJobRules -> MonitoredJobLeases -> MonitoredJobs
            //           FixPolicyRuleSteps -> FixPolicyRules
            //           ClassificationRules
            migrationBuilder.Sql(@"
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
");

            // Add the Exe job type used by production jobs (e.g. Nsg-Events).
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM [dbo].[JobTypes] WHERE [Name] = N'Exe')
    INSERT INTO [dbo].[JobTypes] ([Name], [Description], [IsActive])
    VALUES (N'Exe', N'Native executable / batch process', 1);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the Exe job type added in Up (only if nothing references it).
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM [dbo].[MonitoredJobs] WHERE [JobTypeId] = (SELECT [JobTypeId] FROM [dbo].[JobTypes] WHERE [Name] = N'Exe'))
AND NOT EXISTS (SELECT 1 FROM [dbo].[ClassificationRules] WHERE [JobTypeId] = (SELECT [JobTypeId] FROM [dbo].[JobTypes] WHERE [Name] = N'Exe'))
AND NOT EXISTS (SELECT 1 FROM [dbo].[FixPolicyRules] WHERE [JobTypeId] = (SELECT [JobTypeId] FROM [dbo].[JobTypes] WHERE [Name] = N'Exe'))
    DELETE FROM [dbo].[JobTypes] WHERE [Name] = N'Exe';
");
            // Note: the dev seed rows deleted in Up are not restored here —
            // Down() is a rollback path for schema changes, not a data restore.
        }
    }
}
