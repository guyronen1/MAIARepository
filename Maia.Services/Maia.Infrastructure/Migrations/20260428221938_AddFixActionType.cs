using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFixActionType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActionPayload",
                table: "FixPolicyRules",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionType",
                table: "FixPolicyRules",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Manual");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActionPayload",
                table: "FixPolicyRules");

            migrationBuilder.DropColumn(
                name: "ActionType",
                table: "FixPolicyRules");
        }
    }
}
