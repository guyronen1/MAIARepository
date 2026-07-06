using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoredJobLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LeaseDurationSeconds",
                table: "ScanTypes",
                type: "int",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.CreateTable(
                name: "MonitoredJobLeases",
                columns: table => new
                {
                    MonitoredJobId = table.Column<int>(type: "int", nullable: false),
                    LeasedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LeasedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LeasedUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    NextEligibleAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "'0001-01-01'"),
                    LastRunStartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastRunCompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastRunOutcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LastRunError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredJobLeases", x => x.MonitoredJobId);
                    table.ForeignKey(
                        name: "FK_MonitoredJobLeases_MonitoredJobs_MonitoredJobId",
                        column: x => x.MonitoredJobId,
                        principalTable: "MonitoredJobs",
                        principalColumn: "MonitoredJobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "MonitoredJobLeases",
                columns: new[] { "MonitoredJobId", "LastRunCompletedAt", "LastRunError", "LastRunOutcome", "LastRunStartedAt", "LeasedAt", "LeasedBy", "LeasedUntil" },
                values: new object[] { 1, null, null, null, null, null, null, null });

            migrationBuilder.UpdateData(
                table: "ScanTypes",
                keyColumn: "ScanTypeId",
                keyValue: 1,
                column: "LeaseDurationSeconds",
                value: 300);

            migrationBuilder.UpdateData(
                table: "ScanTypes",
                keyColumn: "ScanTypeId",
                keyValue: 2,
                column: "LeaseDurationSeconds",
                value: 1800);

            migrationBuilder.UpdateData(
                table: "ScanTypes",
                keyColumn: "ScanTypeId",
                keyValue: 3,
                column: "LeaseDurationSeconds",
                value: 60);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredJobLeases_Eligible",
                table: "MonitoredJobLeases",
                columns: new[] { "NextEligibleAt", "LeasedUntil" });

            // Backfill: ensure every existing MonitoredJob has a lease row.
            // Idempotent — the LEFT JOIN guarantees no duplicate keys.
            migrationBuilder.Sql("""
                INSERT INTO dbo.MonitoredJobLeases (MonitoredJobId, NextEligibleAt)
                SELECT m.MonitoredJobId, '0001-01-01'
                FROM dbo.MonitoredJobs m
                LEFT JOIN dbo.MonitoredJobLeases l ON l.MonitoredJobId = m.MonitoredJobId
                WHERE l.MonitoredJobId IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoredJobLeases");

            migrationBuilder.DropColumn(
                name: "LeaseDurationSeconds",
                table: "ScanTypes");
        }
    }
}
