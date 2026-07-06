using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScanDbWatermarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WatermarkColumn",
                table: "ScanCheckRules",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScanDbWatermarks",
                columns: table => new
                {
                    WatermarkId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CheckRuleId = table.Column<int>(type: "int", nullable: false),
                    WatermarkValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanDbWatermarks", x => x.WatermarkId);
                    table.ForeignKey(
                        name: "FK_ScanDbWatermarks_ScanCheckRules_CheckRuleId",
                        column: x => x.CheckRuleId,
                        principalTable: "ScanCheckRules",
                        principalColumn: "CheckRuleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScanDbWatermarks_CheckRuleId",
                table: "ScanDbWatermarks",
                column: "CheckRuleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanDbWatermarks");

            migrationBuilder.DropColumn(
                name: "WatermarkColumn",
                table: "ScanCheckRules");
        }
    }
}
