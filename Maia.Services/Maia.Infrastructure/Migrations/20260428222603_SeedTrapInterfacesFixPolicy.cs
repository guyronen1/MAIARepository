using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedTrapInterfacesFixPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "FixPolicyRules",
                columns: new[] { "RuleId", "ActionPayload", "ActionTimestamp", "ActionToApply", "ActionType", "CreatedBy", "Enabled", "ErrorTypeId", "FixCategory", "IsAutoHealEligible", "JobTypeId" },
                values: new object[] { 1, "http://jobs.internal/api/jobs/{failureId}/retry", new DateTime(2026, 4, 28, 0, 0, 0, 0, DateTimeKind.Utc), "Retry DTSX job via job-management API", "ApiCall", "System", true, 3, "Retry", true, 1 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "FixPolicyRules",
                keyColumn: "RuleId",
                keyValue: 1);
        }
    }
}
