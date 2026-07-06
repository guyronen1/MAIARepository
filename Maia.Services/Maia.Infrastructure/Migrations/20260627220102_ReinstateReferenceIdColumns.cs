using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReinstateReferenceIdColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Reinstates the columns the orphaned 20260620120000_AddReferenceIdColumn
            // migration was meant to add (it shipped without its .Designer.cs, so EF
            // never recognised it). The model snapshot already carried these columns,
            // so the scaffolded diff was empty — the operations are written by hand.
            migrationBuilder.AddColumn<string>(
                name: "ReferenceId",
                table: "JobFailures",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceIdColumn",
                table: "ScanCheckRules",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferenceId",
                table: "JobFailures");

            migrationBuilder.DropColumn(
                name: "ReferenceIdColumn",
                table: "ScanCheckRules");
        }
    }
}
