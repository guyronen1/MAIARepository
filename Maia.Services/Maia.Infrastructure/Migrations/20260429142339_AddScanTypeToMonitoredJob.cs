using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScanTypeToMonitoredJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckColumn",
                table: "MonitoredJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogSourceUrl",
                table: "MonitoredJobs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RangeMax",
                table: "MonitoredJobs",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RangeMin",
                table: "MonitoredJobs",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanType",
                table: "MonitoredJobs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "FileSystem");

            migrationBuilder.UpdateData(
                table: "MonitoredJobs",
                keyColumn: "MonitoredJobId",
                keyValue: 1,
                columns: new[] { "CheckColumn", "LogSourceUrl", "RangeMax", "RangeMin" },
                values: new object[] { null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckColumn",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "LogSourceUrl",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "RangeMax",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "RangeMin",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "ScanType",
                table: "MonitoredJobs");
        }
    }
}
