using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Defense-in-depth for the composite-policy invariants that
    /// <c>ConfigController.ValidateCompositePayload</c> (now on
    /// <c>FixPolicyConfigController</c>) enforces at the application layer — guards
    /// against direct SQL INSERTs bypassing the controller (mirrors the filtered-unique-
    /// index rationale on FixPolicyRules). <c>ActionType</c> is persisted as its enum NAME
    /// (<c>HasConversion&lt;string&gt;</c>), so these compare against the string names.
    /// </summary>
    public partial class AddCompositeInvariantCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A composite STEP can never be Manual or Composite — no nesting, and a
            // composite is automated by definition (a Manual step is meaningless).
            migrationBuilder.Sql(@"
                ALTER TABLE [dbo].[FixPolicyRuleSteps] WITH CHECK
                ADD CONSTRAINT [CK_FixPolicyRuleSteps_ActionType_NotManualOrComposite]
                CHECK ([ActionType] NOT IN ('Manual', 'Composite'));");

            // A step must carry a non-blank payload (whitespace-only is not a payload).
            migrationBuilder.Sql(@"
                ALTER TABLE [dbo].[FixPolicyRuleSteps] WITH CHECK
                ADD CONSTRAINT [CK_FixPolicyRuleSteps_ActionPayload_NotEmpty]
                CHECK (LEN(LTRIM(RTRIM([ActionPayload]))) > 0);");

            // A Composite policy HEADER carries no payload of its own — payload lives on
            // each step. Non-composite rows are unconstrained by this check.
            migrationBuilder.Sql(@"
                ALTER TABLE [dbo].[FixPolicyRules] WITH CHECK
                ADD CONSTRAINT [CK_FixPolicyRules_CompositeHeader_NullPayload]
                CHECK ([ActionType] <> 'Composite' OR [ActionPayload] IS NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE [dbo].[FixPolicyRuleSteps] DROP CONSTRAINT IF EXISTS [CK_FixPolicyRuleSteps_ActionType_NotManualOrComposite];");
            migrationBuilder.Sql(
                "ALTER TABLE [dbo].[FixPolicyRuleSteps] DROP CONSTRAINT IF EXISTS [CK_FixPolicyRuleSteps_ActionPayload_NotEmpty];");
            migrationBuilder.Sql(
                "ALTER TABLE [dbo].[FixPolicyRules] DROP CONSTRAINT IF EXISTS [CK_FixPolicyRules_CompositeHeader_NullPayload];");
        }
    }
}
