using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClassificationRuleActiveKeyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClassificationRules_JobTypeId",
                table: "ClassificationRules");

            migrationBuilder.CreateIndex(
                name: "UX_ClassificationRules_ActiveKey",
                table: "ClassificationRules",
                columns: new[] { "JobTypeId", "Pattern" },
                unique: true,
                filter: "[IsActive] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ClassificationRules_ActiveKey",
                table: "ClassificationRules");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationRules_JobTypeId",
                table: "ClassificationRules",
                column: "JobTypeId");
        }
    }
}
