using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefactorMonitoredJobLogPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename the combined path+pattern column to a folder-only column
            migrationBuilder.RenameColumn(
                name:  "LogPathTemplate",
                table: "MonitoredJobs",
                newName: "LogFolder");

            // Add the new comma-separated patterns column
            migrationBuilder.AddColumn<string>(
                name:      "SearchPatterns",
                table:     "MonitoredJobs",
                type:      "nvarchar(500)",
                maxLength: 500,
                nullable:  true);

            // Migrate existing TrapInterfaces row: strip filename from old combined value
            migrationBuilder.Sql(@"
                UPDATE MonitoredJobs
                SET    LogFolder      = 'c:\logs',
                       SearchPatterns = 'Trap*.log'
                WHERE  MonitoredJobId = 1
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
