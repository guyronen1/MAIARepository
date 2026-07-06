using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedTrapInterfaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "MonitoredJobs",
                columns: new[] { "MonitoredJobId", "ConnectionName", "CreatedAt", "Description", "DisplayName", "IsActive", "JobTypeId", "LogPathTemplate", "Name", "PollingIntervalSeconds", "SourceTable" },
                values: new object[] { 1, null, new DateTime(2026, 4, 28, 0, 0, 0, 0, DateTimeKind.Utc), "עיבוד ניודים נכנסים", "Trap Interfaces", true, 1, "c:\\logs\\Trap*.log", "TrapInterfaces", 300, null });

            migrationBuilder.InsertData(
                table: "MonitoredJobRules",
                columns: new[] { "JobRuleId", "IsActive", "MonitoredJobId", "RuleId" },
                values: new object[] { 1, true, 1, 4 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "MonitoredJobRules",
                keyColumn: "JobRuleId",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "MonitoredJobs",
                keyColumn: "MonitoredJobId",
                keyValue: 1);
        }
    }
}
