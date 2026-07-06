using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleSuggestionProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SuggestedBy",
                table: "FixPolicyRules",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SuggestedConfidence",
                table: "FixPolicyRules",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedFromHash",
                table: "FixPolicyRules",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedBy",
                table: "ClassificationRules",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SuggestedConfidence",
                table: "ClassificationRules",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedFromHash",
                table: "ClassificationRules",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ClassificationRules",
                keyColumn: "RuleId",
                keyValue: 1,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "ClassificationRules",
                keyColumn: "RuleId",
                keyValue: 2,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "ClassificationRules",
                keyColumn: "RuleId",
                keyValue: 3,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "ClassificationRules",
                keyColumn: "RuleId",
                keyValue: 4,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "ClassificationRules",
                keyColumn: "RuleId",
                keyValue: 5,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "ClassificationRules",
                keyColumn: "RuleId",
                keyValue: 6,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "ClassificationRules",
                keyColumn: "RuleId",
                keyValue: 7,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "ClassificationRules",
                keyColumn: "RuleId",
                keyValue: 8,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "FixPolicyRules",
                keyColumn: "RuleId",
                keyValue: 1,
                columns: new[] { "SuggestedBy", "SuggestedConfidence", "SuggestedFromHash" },
                values: new object[] { null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SuggestedBy",
                table: "FixPolicyRules");

            migrationBuilder.DropColumn(
                name: "SuggestedConfidence",
                table: "FixPolicyRules");

            migrationBuilder.DropColumn(
                name: "SuggestedFromHash",
                table: "FixPolicyRules");

            migrationBuilder.DropColumn(
                name: "SuggestedBy",
                table: "ClassificationRules");

            migrationBuilder.DropColumn(
                name: "SuggestedConfidence",
                table: "ClassificationRules");

            migrationBuilder.DropColumn(
                name: "SuggestedFromHash",
                table: "ClassificationRules");
        }
    }
}
