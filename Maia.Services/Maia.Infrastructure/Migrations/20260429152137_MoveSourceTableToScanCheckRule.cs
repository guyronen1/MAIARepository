using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoveSourceTableToScanCheckRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceTable",
                table: "MonitoredJobs");

            migrationBuilder.AddColumn<string>(
                name: "SourceTable",
                table: "ScanCheckRules",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceTable",
                table: "ScanCheckRules");

            migrationBuilder.AddColumn<string>(
                name: "SourceTable",
                table: "MonitoredJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "MonitoredJobs",
                keyColumn: "MonitoredJobId",
                keyValue: 1,
                column: "SourceTable",
                value: null);
        }
    }
}
