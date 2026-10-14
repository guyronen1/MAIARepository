using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Widens CK_ScanCheckRuleConditions_Kind_Allowed for the Database check kinds added
    /// 2026-10-14 (NotEquals, Contains, InList), so they can be extra AND'd conditions as
    /// well as a rule's own check. No model change: the constraint lives in raw SQL (see
    /// AddScanCheckRuleConditions) and ScanCheckRules.CheckType is an unconstrained
    /// nvarchar enum name. Also hand-written into Scripts/generate-clean-schema.ps1,
    /// schema-clean.sql and upgrade-existing-to-20261012.sql — keep in sync.
    /// </summary>
    public partial class AllowNewDbConditionKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE [dbo].[ScanCheckRuleConditions]
                DROP CONSTRAINT IF EXISTS [CK_ScanCheckRuleConditions_Kind_Allowed];
                ALTER TABLE [dbo].[ScanCheckRuleConditions] WITH CHECK
                ADD CONSTRAINT [CK_ScanCheckRuleConditions_Kind_Allowed]
                CHECK ([Kind] IN ('ValueEquals', 'ColumnRange', 'NotEquals', 'Contains', 'InList', 'FileContent', 'ErrorKeyword'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fails (by design) while any condition still uses a new kind.
            migrationBuilder.Sql(@"
                ALTER TABLE [dbo].[ScanCheckRuleConditions]
                DROP CONSTRAINT IF EXISTS [CK_ScanCheckRuleConditions_Kind_Allowed];
                ALTER TABLE [dbo].[ScanCheckRuleConditions] WITH CHECK
                ADD CONSTRAINT [CK_ScanCheckRuleConditions_Kind_Allowed]
                CHECK ([Kind] IN ('ValueEquals', 'ColumnRange', 'FileContent', 'ErrorKeyword'));");
        }
    }
}
