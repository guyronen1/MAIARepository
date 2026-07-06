using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFixPolicyMonitoredJobOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_FixPolicyRules_ActiveKey",
                table: "FixPolicyRules");

            migrationBuilder.AddColumn<int>(
                name: "MonitoredJobId",
                table: "FixPolicyRules",
                type: "int",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "FixPolicyRules",
                keyColumn: "RuleId",
                keyValue: 1,
                column: "MonitoredJobId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "UX_FixPolicyRules_DefaultActiveKey",
                table: "FixPolicyRules",
                columns: new[] { "JobTypeId", "ErrorTypeId" },
                unique: true,
                filter: "[Enabled] = 1 AND [MonitoredJobId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_FixPolicyRules_OverrideActiveKey",
                table: "FixPolicyRules",
                columns: new[] { "MonitoredJobId", "ErrorTypeId" },
                unique: true,
                filter: "[Enabled] = 1 AND [MonitoredJobId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_FixPolicyRules_MonitoredJobs_MonitoredJobId",
                table: "FixPolicyRules",
                column: "MonitoredJobId",
                principalTable: "MonitoredJobs",
                principalColumn: "MonitoredJobId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FixPolicyRules_MonitoredJobs_MonitoredJobId",
                table: "FixPolicyRules");

            migrationBuilder.DropIndex(
                name: "UX_FixPolicyRules_DefaultActiveKey",
                table: "FixPolicyRules");

            migrationBuilder.DropIndex(
                name: "UX_FixPolicyRules_OverrideActiveKey",
                table: "FixPolicyRules");

            migrationBuilder.DropColumn(
                name: "MonitoredJobId",
                table: "FixPolicyRules");

            migrationBuilder.CreateIndex(
                name: "UX_FixPolicyRules_ActiveKey",
                table: "FixPolicyRules",
                columns: new[] { "JobTypeId", "ErrorTypeId" },
                unique: true,
                filter: "[Enabled] = 1");
        }
    }
}
