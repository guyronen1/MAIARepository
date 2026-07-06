using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecommendationAtomicClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "AIRecommendations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "AIRecommendations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AIRecommendations_ClaimEligible",
                table: "AIRecommendations",
                columns: new[] { "IsExecuted", "ClaimedAt" },
                filter: "[IsExecuted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AIRecommendations_ClaimEligible",
                table: "AIRecommendations");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "AIRecommendations");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "AIRecommendations");
        }
    }
}
