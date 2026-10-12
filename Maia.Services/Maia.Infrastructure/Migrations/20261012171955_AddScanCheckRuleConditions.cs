using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScanCheckRuleConditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScanCheckRuleConditions",
                columns: table => new
                {
                    ConditionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CheckRuleId = table.Column<int>(type: "int", nullable: false),
                    ConditionOrder = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TargetField = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ExpectedValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MinValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PredicateType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanCheckRuleConditions", x => x.ConditionId);
                    table.ForeignKey(
                        name: "FK_ScanCheckRuleConditions_ScanCheckRules_CheckRuleId",
                        column: x => x.CheckRuleId,
                        principalTable: "ScanCheckRules",
                        principalColumn: "CheckRuleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ScanCheckRuleConditions_CheckRuleId_ConditionOrder",
                table: "ScanCheckRuleConditions",
                columns: new[] { "CheckRuleId", "ConditionOrder" },
                unique: true);

            // Defense-in-depth for what ScanRulesConfigController.ApplyAndValidateConditions
            // enforces (mirrors AddCompositeInvariantCheckConstraints). Kind is persisted as
            // its enum NAME (HasConversion<string>). The allow-list already includes the
            // Phase 2 kinds (FileContent / ErrorKeyword) so that phase needs no migration;
            // SqlQuery / StatusCode / ResponseContains can never be a condition.
            // Also hand-written into Scripts/generate-clean-schema.ps1 — keep in sync.
            migrationBuilder.Sql(@"
                ALTER TABLE [dbo].[ScanCheckRuleConditions] WITH CHECK
                ADD CONSTRAINT [CK_ScanCheckRuleConditions_Kind_Allowed]
                CHECK ([Kind] IN ('ValueEquals', 'ColumnRange', 'FileContent', 'ErrorKeyword'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE [dbo].[ScanCheckRuleConditions]
                DROP CONSTRAINT IF EXISTS [CK_ScanCheckRuleConditions_Kind_Allowed];");

            migrationBuilder.DropTable(
                name: "ScanCheckRuleConditions");
        }
    }
}
