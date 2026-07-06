using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScanRunHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScanRunHistory",
                columns: table => new
                {
                    ScanRunId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MonitoredJobId = table.Column<int>(type: "int", nullable: false),
                    LeasedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FailuresDetected = table.Column<int>(type: "int", nullable: false),
                    Classifications = table.Column<int>(type: "int", nullable: false),
                    Recommendations = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanRunHistory", x => x.ScanRunId);
                    table.ForeignKey(
                        name: "FK_ScanRunHistory_MonitoredJobs_MonitoredJobId",
                        column: x => x.MonitoredJobId,
                        principalTable: "MonitoredJobs",
                        principalColumn: "MonitoredJobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScanRunHistory_Failures",
                table: "ScanRunHistory",
                column: "StartedAt",
                descending: new bool[0],
                filter: "[Outcome] <> 'Success'")
                .Annotation("SqlServer:Include", new[] { "MonitoredJobId", "Outcome", "Error" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanRunHistory_Job_StartedAt",
                table: "ScanRunHistory",
                columns: new[] { "MonitoredJobId", "StartedAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "CompletedAt", "DurationMs", "Outcome", "FailuresDetected", "Classifications", "Recommendations" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanRunHistory");
        }
    }
}
