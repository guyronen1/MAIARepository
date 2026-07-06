using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScanWatermarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScanFileWatermarks",
                columns: table => new
                {
                    WatermarkId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MonitoredJobId = table.Column<int>(type: "int", nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ByteOffset = table.Column<long>(type: "bigint", nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanFileWatermarks", x => x.WatermarkId);
                    table.ForeignKey(
                        name: "FK_ScanFileWatermarks_MonitoredJobs_MonitoredJobId",
                        column: x => x.MonitoredJobId,
                        principalTable: "MonitoredJobs",
                        principalColumn: "MonitoredJobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScanFileWatermarks_MonitoredJobId_FilePath",
                table: "ScanFileWatermarks",
                columns: new[] { "MonitoredJobId", "FilePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanFileWatermarks");
        }
    }
}
