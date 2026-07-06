using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScanSourceEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScanSourceId",
                table: "ScanRunHistory",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanSourceId",
                table: "ScanFileWatermarks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanSourceId",
                table: "ScanContentWatermarks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanSourceId",
                table: "ScanCheckRules",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanSourceId",
                table: "JobFailures",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScanSources",
                columns: table => new
                {
                    ScanSourceId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MonitoredJobId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ScanTypeId = table.Column<int>(type: "int", nullable: false),
                    LogFolder = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SearchPatterns = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InputFolder = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IncludeSubfolders = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ConnectionName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LogSourceUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PollingIntervalSeconds = table.Column<int>(type: "int", nullable: false, defaultValue: 300),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanSources", x => x.ScanSourceId);
                    table.ForeignKey(
                        name: "FK_ScanSources_MonitoredJobs_MonitoredJobId",
                        column: x => x.MonitoredJobId,
                        principalTable: "MonitoredJobs",
                        principalColumn: "MonitoredJobId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScanSources_ScanTypes_ScanTypeId",
                        column: x => x.ScanTypeId,
                        principalTable: "ScanTypes",
                        principalColumn: "ScanTypeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScanRunHistory_Source_StartedAt",
                table: "ScanRunHistory",
                columns: new[] { "ScanSourceId", "StartedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ScanFileWatermarks_ScanSourceId",
                table: "ScanFileWatermarks",
                column: "ScanSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanContentWatermarks_ScanSourceId",
                table: "ScanContentWatermarks",
                column: "ScanSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanCheckRules_ScanSourceId",
                table: "ScanCheckRules",
                column: "ScanSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_JobFailures_ScanSourceId",
                table: "JobFailures",
                column: "ScanSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanSources_MonitoredJobId",
                table: "ScanSources",
                column: "MonitoredJobId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanSources_ScanTypeId",
                table: "ScanSources",
                column: "ScanTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_JobFailures_ScanSources_ScanSourceId",
                table: "JobFailures",
                column: "ScanSourceId",
                principalTable: "ScanSources",
                principalColumn: "ScanSourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScanCheckRules_ScanSources_ScanSourceId",
                table: "ScanCheckRules",
                column: "ScanSourceId",
                principalTable: "ScanSources",
                principalColumn: "ScanSourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScanContentWatermarks_ScanSources_ScanSourceId",
                table: "ScanContentWatermarks",
                column: "ScanSourceId",
                principalTable: "ScanSources",
                principalColumn: "ScanSourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScanFileWatermarks_ScanSources_ScanSourceId",
                table: "ScanFileWatermarks",
                column: "ScanSourceId",
                principalTable: "ScanSources",
                principalColumn: "ScanSourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScanRunHistory_ScanSources_ScanSourceId",
                table: "ScanRunHistory",
                column: "ScanSourceId",
                principalTable: "ScanSources",
                principalColumn: "ScanSourceId");

            // ── Backfill (behavior-preserving) ──────────────────────────────────
            // One ScanSource per existing MonitoredJob, mirroring its config 1:1
            // (Name = its ScanType's name). Then point every child row at that
            // single source. JobFailures.MonitoredJobId is nullable — orphan
            // failures (NULL) keep a NULL ScanSourceId. Dropped automatically on
            // Down() when the table + columns are removed.
            migrationBuilder.Sql("""
                INSERT INTO ScanSources
                    (MonitoredJobId, Name, ScanTypeId, LogFolder, SearchPatterns, InputFolder,
                     IncludeSubfolders, ConnectionName, LogSourceUrl, PollingIntervalSeconds, IsActive)
                SELECT
                    j.MonitoredJobId, st.Name, j.ScanTypeId, j.LogFolder, j.SearchPatterns, j.InputFolder,
                    j.IncludeSubfolders, j.ConnectionName, j.LogSourceUrl, j.PollingIntervalSeconds, j.IsActive
                FROM MonitoredJobs j
                JOIN ScanTypes st ON st.ScanTypeId = j.ScanTypeId;
                """);

            migrationBuilder.Sql("""
                UPDATE r SET r.ScanSourceId = s.ScanSourceId
                FROM ScanCheckRules r JOIN ScanSources s ON s.MonitoredJobId = r.MonitoredJobId;
                """);
            migrationBuilder.Sql("""
                UPDATE w SET w.ScanSourceId = s.ScanSourceId
                FROM ScanFileWatermarks w JOIN ScanSources s ON s.MonitoredJobId = w.MonitoredJobId;
                """);
            migrationBuilder.Sql("""
                UPDATE w SET w.ScanSourceId = s.ScanSourceId
                FROM ScanContentWatermarks w JOIN ScanSources s ON s.MonitoredJobId = w.MonitoredJobId;
                """);
            migrationBuilder.Sql("""
                UPDATE h SET h.ScanSourceId = s.ScanSourceId
                FROM ScanRunHistory h JOIN ScanSources s ON s.MonitoredJobId = h.MonitoredJobId;
                """);
            migrationBuilder.Sql("""
                UPDATE f SET f.ScanSourceId = s.ScanSourceId
                FROM JobFailures f JOIN ScanSources s ON s.MonitoredJobId = f.MonitoredJobId;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobFailures_ScanSources_ScanSourceId",
                table: "JobFailures");

            migrationBuilder.DropForeignKey(
                name: "FK_ScanCheckRules_ScanSources_ScanSourceId",
                table: "ScanCheckRules");

            migrationBuilder.DropForeignKey(
                name: "FK_ScanContentWatermarks_ScanSources_ScanSourceId",
                table: "ScanContentWatermarks");

            migrationBuilder.DropForeignKey(
                name: "FK_ScanFileWatermarks_ScanSources_ScanSourceId",
                table: "ScanFileWatermarks");

            migrationBuilder.DropForeignKey(
                name: "FK_ScanRunHistory_ScanSources_ScanSourceId",
                table: "ScanRunHistory");

            migrationBuilder.DropTable(
                name: "ScanSources");

            migrationBuilder.DropIndex(
                name: "IX_ScanRunHistory_Source_StartedAt",
                table: "ScanRunHistory");

            migrationBuilder.DropIndex(
                name: "IX_ScanFileWatermarks_ScanSourceId",
                table: "ScanFileWatermarks");

            migrationBuilder.DropIndex(
                name: "IX_ScanContentWatermarks_ScanSourceId",
                table: "ScanContentWatermarks");

            migrationBuilder.DropIndex(
                name: "IX_ScanCheckRules_ScanSourceId",
                table: "ScanCheckRules");

            migrationBuilder.DropIndex(
                name: "IX_JobFailures_ScanSourceId",
                table: "JobFailures");

            migrationBuilder.DropColumn(
                name: "ScanSourceId",
                table: "ScanRunHistory");

            migrationBuilder.DropColumn(
                name: "ScanSourceId",
                table: "ScanFileWatermarks");

            migrationBuilder.DropColumn(
                name: "ScanSourceId",
                table: "ScanContentWatermarks");

            migrationBuilder.DropColumn(
                name: "ScanSourceId",
                table: "ScanCheckRules");

            migrationBuilder.DropColumn(
                name: "ScanSourceId",
                table: "JobFailures");
        }
    }
}
