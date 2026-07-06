using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Tier25CleanupColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Part A: remove orphan rows that have no ScanSourceId (test-data only;
            // production data is already fully backfilled from the Tier 2.5 phase-a migration).
            migrationBuilder.Sql(@"
                DELETE FROM [dbo].[ScanContentWatermarks] WHERE [ScanSourceId] IS NULL;
                DELETE FROM [dbo].[ScanFileWatermarks]    WHERE [ScanSourceId] IS NULL;
                DELETE FROM [dbo].[ScanRunHistory]        WHERE [ScanSourceId] IS NULL;
            ");

            // Part C: enforce NOT NULL on every ScanSourceId column now that orphan
            // rows are gone and all new inserts are source-scoped.
            // SQL Server requires dropping any indexes on the column first, then
            // altering, then recreating.
            migrationBuilder.Sql(@"
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
            ");

            migrationBuilder.DropForeignKey(
                name: "FK_MonitoredJobs_ScanTypes_ScanTypeId",
                table: "MonitoredJobs");

            migrationBuilder.DropIndex(
                name: "IX_MonitoredJobs_ScanTypeId",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "ConnectionName",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "IncludeSubfolders",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "InputFolder",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "LogFolder",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "LogSourceUrl",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "ScanTypeId",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "SearchPatterns",
                table: "MonitoredJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConnectionName",
                table: "MonitoredJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludeSubfolders",
                table: "MonitoredJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InputFolder",
                table: "MonitoredJobs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogFolder",
                table: "MonitoredJobs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogSourceUrl",
                table: "MonitoredJobs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanTypeId",
                table: "MonitoredJobs",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "SearchPatterns",
                table: "MonitoredJobs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "MonitoredJobs",
                keyColumn: "MonitoredJobId",
                keyValue: 1,
                columns: new[] { "ConnectionName", "InputFolder", "LogFolder", "LogSourceUrl", "ScanTypeId", "SearchPatterns" },
                values: new object[] { null, null, "c:\\logs", null, 1, "Trap*.log" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredJobs_ScanTypeId",
                table: "MonitoredJobs",
                column: "ScanTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_MonitoredJobs_ScanTypes_ScanTypeId",
                table: "MonitoredJobs",
                column: "ScanTypeId",
                principalTable: "ScanTypes",
                principalColumn: "ScanTypeId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
