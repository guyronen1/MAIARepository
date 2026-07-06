using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompositeFixesAndSourceFilePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilePathColumn",
                table: "ScanCheckRules",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputPathPattern",
                table: "ScanCheckRules",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputFolder",
                table: "MonitoredJobs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFilePath",
                table: "JobFailures",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FixPolicyRuleSteps",
                columns: table => new
                {
                    StepId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    StepOrder = table.Column<int>(type: "int", nullable: false),
                    ActionType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ActionPayload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixPolicyRuleSteps", x => x.StepId);
                    table.ForeignKey(
                        name: "FK_FixPolicyRuleSteps_FixPolicyRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "FixPolicyRules",
                        principalColumn: "RuleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "MonitoredJobs",
                keyColumn: "MonitoredJobId",
                keyValue: 1,
                column: "InputFolder",
                value: null);

            migrationBuilder.CreateIndex(
                name: "UX_FixPolicyRuleSteps_RuleId_StepOrder",
                table: "FixPolicyRuleSteps",
                columns: new[] { "RuleId", "StepOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FixPolicyRuleSteps");

            migrationBuilder.DropColumn(
                name: "FilePathColumn",
                table: "ScanCheckRules");

            migrationBuilder.DropColumn(
                name: "InputPathPattern",
                table: "ScanCheckRules");

            migrationBuilder.DropColumn(
                name: "InputFolder",
                table: "MonitoredJobs");

            migrationBuilder.DropColumn(
                name: "SourceFilePath",
                table: "JobFailures");
        }
    }
}
