using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFixPolicyActiveKeyUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FixPolicyRules_JobTypeId",
                table: "FixPolicyRules");

            migrationBuilder.CreateIndex(
                name: "UX_FixPolicyRules_ActiveKey",
                table: "FixPolicyRules",
                columns: new[] { "JobTypeId", "ErrorTypeId" },
                unique: true,
                filter: "[Enabled] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_FixPolicyRules_ActiveKey",
                table: "FixPolicyRules");

            migrationBuilder.CreateIndex(
                name: "IX_FixPolicyRules_JobTypeId",
                table: "FixPolicyRules",
                column: "JobTypeId");
        }
    }
}
