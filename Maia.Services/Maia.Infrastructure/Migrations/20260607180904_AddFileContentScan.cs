using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFileContentScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IdentifierExtractionFailures",
                table: "ScanRunHistory",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OversizeFileSkips",
                table: "ScanRunHistory",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExtractorLocator",
                table: "ScanCheckRules",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractorPredicateType",
                table: "ScanCheckRules",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractorPredicateValue",
                table: "ScanCheckRules",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractorType",
                table: "ScanCheckRules",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentifierLocator",
                table: "ScanCheckRules",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludeSubfolders",
                table: "MonitoredJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ScanContentWatermarks",
                columns: table => new
                {
                    WatermarkId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MonitoredJobId = table.Column<int>(type: "int", nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    LastModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanContentWatermarks", x => x.WatermarkId);
                    table.ForeignKey(
                        name: "FK_ScanContentWatermarks_MonitoredJobs_MonitoredJobId",
                        column: x => x.MonitoredJobId,
                        principalTable: "MonitoredJobs",
                        principalColumn: "MonitoredJobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "ScanTypes",
                columns: new[] { "ScanTypeId", "Description", "LeaseDurationSeconds", "Name" },
                values: new object[] { 4, "Structured extraction from input data files (XML, …)", 300, "FileContent" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanContentWatermarks_MonitoredJobId_FilePath",
                table: "ScanContentWatermarks",
                columns: new[] { "MonitoredJobId", "FilePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanContentWatermarks");

            migrationBuilder.DeleteData(
                table: "ScanTypes",
                keyColumn: "ScanTypeId",
                keyValue: 4);

            migrationBuilder.DropColumn(
                name: "IdentifierExtractionFailures",
                table: "ScanRunHistory");

            migrationBuilder.DropColumn(
                name: "OversizeFileSkips",
                table: "ScanRunHistory");

            migrationBuilder.DropColumn(
                name: "ExtractorLocator",
                table: "ScanCheckRules");

            migrationBuilder.DropColumn(
                name: "ExtractorPredicateType",
                table: "ScanCheckRules");

            migrationBuilder.DropColumn(
                name: "ExtractorPredicateValue",
                table: "ScanCheckRules");

            migrationBuilder.DropColumn(
                name: "ExtractorType",
                table: "ScanCheckRules");

            migrationBuilder.DropColumn(
                name: "IdentifierLocator",
                table: "ScanCheckRules");

            migrationBuilder.DropColumn(
                name: "IncludeSubfolders",
                table: "MonitoredJobs");
        }
    }
}
