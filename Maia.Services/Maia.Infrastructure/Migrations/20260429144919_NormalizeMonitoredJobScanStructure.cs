using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeMonitoredJobScanStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckColumn",
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

            migrationBuilder.AddColumn<int>(
                name: "ScanTypeId",
                table: "MonitoredJobs",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "ScanCheckRules",
                columns: table => new
                {
                    CheckRuleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MonitoredJobId = table.Column<int>(type: "int", nullable: false),
                    CheckType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TargetField = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MinValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ExpectedValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanCheckRules", x => x.CheckRuleId);
                    table.ForeignKey(
                        name: "FK_ScanCheckRules_MonitoredJobs_MonitoredJobId",
                        column: x => x.MonitoredJobId,
                        principalTable: "MonitoredJobs",
                        principalColumn: "MonitoredJobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScanTypes",
                columns: table => new
                {
                    ScanTypeId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanTypes", x => x.ScanTypeId);
                });

            migrationBuilder.UpdateData(
                table: "MonitoredJobs",
                keyColumn: "MonitoredJobId",
                keyValue: 1,
                column: "ScanTypeId",
                value: 1);

            migrationBuilder.InsertData(
                table: "ScanTypes",
                columns: new[] { "ScanTypeId", "Description", "Name" },
                values: new object[,]
                {
                    { 1, "Scan log files in a folder matching glob patterns", "FileSystem" },
                    { 2, "Query a SQL table and check column values against rules", "Database" },
                    { 3, "Poll an HTTP endpoint and inspect the response", "ApiEndpoint" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredJobs_ScanTypeId",
                table: "MonitoredJobs",
                column: "ScanTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanCheckRules_MonitoredJobId",
                table: "ScanCheckRules",
                column: "MonitoredJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_MonitoredJobs_ScanTypes_ScanTypeId",
                table: "MonitoredJobs",
                column: "ScanTypeId",
                principalTable: "ScanTypes",
                principalColumn: "ScanTypeId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MonitoredJobs_ScanTypes_ScanTypeId",
                table: "MonitoredJobs");

            migrationBuilder.DropTable(
                name: "ScanCheckRules");

            migrationBuilder.DropTable(
                name: "ScanTypes");

            migrationBuilder.DropIndex(
                name: "IX_MonitoredJobs_ScanTypeId",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "ScanTypeId",
                table: "MonitoredJobs");

            migrationBuilder.AddColumn<string>(
                name: "CheckColumn",
                table: "MonitoredJobs",
                type: "nvarchar(200)",
                maxLength: 200,
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
                columns: new[] { "CheckColumn", "RangeMax", "RangeMin", "ScanType" },
                values: new object[] { null, null, null, "FileSystem" });
        }
    }
}
