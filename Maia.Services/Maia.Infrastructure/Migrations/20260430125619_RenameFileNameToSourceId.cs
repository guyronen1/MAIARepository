using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameFileNameToSourceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileName",
                table: "JobFailures");

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "JobFailures",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "JobFailures");

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "JobFailures",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);
        }
    }
}
