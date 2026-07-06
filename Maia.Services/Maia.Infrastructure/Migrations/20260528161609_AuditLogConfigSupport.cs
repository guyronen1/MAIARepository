using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Restructures AuditLog to accept both failure-scoped events and config
    /// changes. FailureId becomes nullable (config audits have no failure to
    /// attach to), and EntityType / EntityId columns are added to discriminate
    /// what each row is actually about (FixPolicyRule, MonitoredJob, ErrorType, etc.).
    ///
    /// Existing rows are deleted — the dev-environment audit data so far is
    /// a handful of "OperatorApproved" / "OperatorRejected" entries with no
    /// EntityType/EntityId set, and backfilling them would still leave them
    /// unmodelled. Clean slate is simpler than a half-correct backfill.
    /// </summary>
    public partial class AuditLogConfigSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time wipe: the existing rows predate the EntityType/EntityId
            // model and can't be cleanly populated. Approved by user.
            migrationBuilder.Sql("DELETE FROM [AuditLog];");

            migrationBuilder.AlterColumn<int>(
                name: "FailureId",
                table: "AuditLog",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<string>(
                name: "EntityId",
                table: "AuditLog",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityType",
                table: "AuditLog",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntityId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "EntityType",
                table: "AuditLog");

            migrationBuilder.AlterColumn<int>(
                name: "FailureId",
                table: "AuditLog",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
