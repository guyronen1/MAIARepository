using Maia.Infrastructure.DataAccess;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    // Hand-written (no Designer file). Without these attributes EF never discovered it, so a
    // fresh database skipped it and AddScanSourceEntity failed reading LogFolder/SearchPatterns.
    // Up is guarded so databases that already have the renamed columns treat it as a no-op.
    [DbContext(typeof(MaiaDbContext))]
    [Migration("20260429000001_RefactorMonitoredJobLogPath")]
    public partial class RefactorMonitoredJobLogPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename the combined path+pattern column to a folder-only column
            migrationBuilder.Sql(@"
                IF COL_LENGTH('dbo.MonitoredJobs', 'LogPathTemplate') IS NOT NULL
                   AND COL_LENGTH('dbo.MonitoredJobs', 'LogFolder') IS NULL
                    EXEC sp_rename 'dbo.MonitoredJobs.LogPathTemplate', 'LogFolder', 'COLUMN';
            ");

            // Add the new comma-separated patterns column
            migrationBuilder.Sql(@"
                IF COL_LENGTH('dbo.MonitoredJobs', 'SearchPatterns') IS NULL
                    ALTER TABLE dbo.MonitoredJobs ADD SearchPatterns nvarchar(500) NULL;
            ");

            // Migrate existing TrapInterfaces row: strip filename from old combined value.
            // EXEC so the batch compiles even before SearchPatterns exists.
            migrationBuilder.Sql(@"
                IF COL_LENGTH('dbo.MonitoredJobs', 'SearchPatterns') IS NOT NULL
                    EXEC(N'UPDATE dbo.MonitoredJobs
                           SET    LogFolder      = ''c:\logs'',
                                  SearchPatterns = ''Trap*.log''
                           WHERE  MonitoredJobId = 1');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore original combined path before dropping the split columns
            migrationBuilder.Sql(@"
                UPDATE MonitoredJobs
                SET    LogFolder = LogFolder + '\' + SearchPatterns
                WHERE  SearchPatterns IS NOT NULL
            ");

            migrationBuilder.DropColumn(
                name:  "SearchPatterns",
                table: "MonitoredJobs");

            migrationBuilder.RenameColumn(
                name:    "LogFolder",
                table:   "MonitoredJobs",
                newName: "LogPathTemplate");
        }
    }
}
