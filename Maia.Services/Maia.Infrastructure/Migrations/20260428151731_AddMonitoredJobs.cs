using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoredJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorTypes",
                columns: table => new
                {
                    ErrorTypeId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorTypes", x => x.ErrorTypeId);
                });

            migrationBuilder.CreateTable(
                name: "JobTypes",
                columns: table => new
                {
                    JobTypeId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTypes", x => x.JobTypeId);
                });

            migrationBuilder.CreateTable(
                name: "ClassificationRules",
                columns: table => new
                {
                    RuleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobTypeId = table.Column<int>(type: "int", nullable: false),
                    ErrorTypeId = table.Column<int>(type: "int", nullable: false),
                    Pattern = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassificationRules", x => x.RuleId);
                    table.ForeignKey(
                        name: "FK_ClassificationRules_ErrorTypes_ErrorTypeId",
                        column: x => x.ErrorTypeId,
                        principalTable: "ErrorTypes",
                        principalColumn: "ErrorTypeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassificationRules_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "JobTypeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FixPolicyRules",
                columns: table => new
                {
                    RuleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobTypeId = table.Column<int>(type: "int", nullable: false),
                    ErrorTypeId = table.Column<int>(type: "int", nullable: false),
                    ActionToApply = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FixCategory = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsAutoHealEligible = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActionTimestamp = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixPolicyRules", x => x.RuleId);
                    table.ForeignKey(
                        name: "FK_FixPolicyRules_ErrorTypes_ErrorTypeId",
                        column: x => x.ErrorTypeId,
                        principalTable: "ErrorTypes",
                        principalColumn: "ErrorTypeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixPolicyRules_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "JobTypeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MonitoredJobs",
                columns: table => new
                {
                    MonitoredJobId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    JobTypeId = table.Column<int>(type: "int", nullable: false),
                    LogPathTemplate = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SourceTable = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConnectionName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PollingIntervalSeconds = table.Column<int>(type: "int", nullable: false, defaultValue: 300),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Description = table.Column<string>(type: "nvarchar(1000)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredJobs", x => x.MonitoredJobId);
                    table.ForeignKey(
                        name: "FK_MonitoredJobs_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "JobTypeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobFailures",
                columns: table => new
                {
                    FailureId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<int>(type: "int", nullable: false),
                    JobTypeId = table.Column<int>(type: "int", nullable: false),
                    ErrorTypeId = table.Column<int>(type: "int", nullable: true),
                    MonitoredJobId = table.Column<int>(type: "int", nullable: true),
                    StepName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    SourceLogPath = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobFailures", x => x.FailureId);
                    table.ForeignKey(
                        name: "FK_JobFailures_ErrorTypes_ErrorTypeId",
                        column: x => x.ErrorTypeId,
                        principalTable: "ErrorTypes",
                        principalColumn: "ErrorTypeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobFailures_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "JobTypeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobFailures_MonitoredJobs_MonitoredJobId",
                        column: x => x.MonitoredJobId,
                        principalTable: "MonitoredJobs",
                        principalColumn: "MonitoredJobId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MonitoredJobRules",
                columns: table => new
                {
                    JobRuleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MonitoredJobId = table.Column<int>(type: "int", nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredJobRules", x => x.JobRuleId);
                    table.ForeignKey(
                        name: "FK_MonitoredJobRules_ClassificationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "ClassificationRules",
                        principalColumn: "RuleId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MonitoredJobRules_MonitoredJobs_MonitoredJobId",
                        column: x => x.MonitoredJobId,
                        principalTable: "MonitoredJobs",
                        principalColumn: "MonitoredJobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AIRecommendations",
                columns: table => new
                {
                    RecommendationId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FailureId = table.Column<int>(type: "int", nullable: false),
                    ErrorTypeId = table.Column<int>(type: "int", nullable: false),
                    SuggestedAction = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FixCategory = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecommendedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    AutoFixAvailable = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    OperatorApproved = table.Column<bool>(type: "bit", nullable: true),
                    IsExecuted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIRecommendations", x => x.RecommendationId);
                    table.ForeignKey(
                        name: "FK_AIRecommendations_ErrorTypes_ErrorTypeId",
                        column: x => x.ErrorTypeId,
                        principalTable: "ErrorTypes",
                        principalColumn: "ErrorTypeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AIRecommendations_JobFailures_FailureId",
                        column: x => x.FailureId,
                        principalTable: "JobFailures",
                        principalColumn: "FailureId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    AuditId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FailureId = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.AuditId);
                    table.ForeignKey(
                        name: "FK_AuditLog_JobFailures_FailureId",
                        column: x => x.FailureId,
                        principalTable: "JobFailures",
                        principalColumn: "FailureId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FixExecutionLog",
                columns: table => new
                {
                    FixId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FailureId = table.Column<int>(type: "int", nullable: false),
                    RecommendationId = table.Column<int>(type: "int", nullable: false),
                    ExecutedAction = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExecutedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ResultDetail = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixExecutionLog", x => x.FixId);
                    table.ForeignKey(
                        name: "FK_FixExecutionLog_AIRecommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "AIRecommendations",
                        principalColumn: "RecommendationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixExecutionLog_JobFailures_FailureId",
                        column: x => x.FailureId,
                        principalTable: "JobFailures",
                        principalColumn: "FailureId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OperatorActions",
                columns: table => new
                {
                    ActionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecommendationId = table.Column<int>(type: "int", nullable: false),
                    OperatorId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActionTaken = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActionTimestamp = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorActions", x => x.ActionId);
                    table.ForeignKey(
                        name: "FK_OperatorActions_AIRecommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "AIRecommendations",
                        principalColumn: "RecommendationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "ErrorTypes",
                columns: new[] { "ErrorTypeId", "Code", "Description", "DisplayName", "IsActive", "Severity" },
                values: new object[,]
                {
                    { 1, "FileNotFound", null, "File Not Found", true, "High" },
                    { 2, "DbConnection", null, "Database Connection Error", true, "High" },
                    { 3, "Timeout", null, "Execution Timeout", true, "Medium" },
                    { 4, "Transform", null, "Data Transform Failure", true, "Medium" },
                    { 5, "Permission", null, "Access Denied", true, "High" },
                    { 6, "Unknown", null, "Unknown Error", true, "Low" }
                });

            migrationBuilder.InsertData(
                table: "JobTypes",
                columns: new[] { "JobTypeId", "Description", "IsActive", "Name" },
                values: new object[,]
                {
                    { 1, "SQL Server Integration Services package", true, "DTSX" },
                    { 2, "SQL Server Agent Job", true, "SqlAgent" },
                    { 3, "Python script", true, "Python" },
                    { 4, "PowerShell script", true, "PowerShell" }
                });

            migrationBuilder.InsertData(
                table: "ClassificationRules",
                columns: new[] { "RuleId", "Confidence", "CreatedBy", "ErrorTypeId", "IsActive", "JobTypeId", "Pattern", "Priority" },
                values: new object[,]
                {
                    { 1, 0.95m, null, 1, true, 1, "FileNotFoundException", 1 },
                    { 2, 0.85m, null, 4, true, 1, "DTS_E_OLEDBERROR", 2 },
                    { 3, 0.93m, null, 2, true, 1, "DTS_E_CANNOTACQUIRECONNECTION", 3 },
                    { 4, 0.88m, null, 3, true, 1, "Timeout expired", 4 },
                    { 5, 0.95m, null, 2, true, 2, "Login failed", 1 },
                    { 6, 0.95m, null, 1, true, 3, "FileNotFoundError", 1 },
                    { 7, 0.90m, null, 2, true, 3, "ConnectionRefusedError", 2 },
                    { 8, 0.88m, null, 5, true, 4, "Access is denied", 1 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AIRecommendations_ErrorTypeId",
                table: "AIRecommendations",
                column: "ErrorTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AIRecommendations_FailureId",
                table: "AIRecommendations",
                column: "FailureId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_FailureId",
                table: "AuditLog",
                column: "FailureId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationRules_ErrorTypeId",
                table: "ClassificationRules",
                column: "ErrorTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationRules_JobTypeId",
                table: "ClassificationRules",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ErrorTypes_Code",
                table: "ErrorTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FixExecutionLog_FailureId",
                table: "FixExecutionLog",
                column: "FailureId");

            migrationBuilder.CreateIndex(
                name: "IX_FixExecutionLog_RecommendationId",
                table: "FixExecutionLog",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_FixPolicyRules_ErrorTypeId",
                table: "FixPolicyRules",
                column: "ErrorTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_FixPolicyRules_JobTypeId",
                table: "FixPolicyRules",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_JobFailures_ErrorTypeId",
                table: "JobFailures",
                column: "ErrorTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_JobFailures_JobTypeId",
                table: "JobFailures",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_JobFailures_MonitoredJobId",
                table: "JobFailures",
                column: "MonitoredJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredJobRules_MonitoredJobId_RuleId",
                table: "MonitoredJobRules",
                columns: new[] { "MonitoredJobId", "RuleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredJobRules_RuleId",
                table: "MonitoredJobRules",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredJobs_JobTypeId",
                table: "MonitoredJobs",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredJobs_Name",
                table: "MonitoredJobs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorActions_RecommendationId",
                table: "OperatorActions",
                column: "RecommendationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "FixExecutionLog");

            migrationBuilder.DropTable(
                name: "FixPolicyRules");

            migrationBuilder.DropTable(
                name: "MonitoredJobRules");

            migrationBuilder.DropTable(
                name: "OperatorActions");

            migrationBuilder.DropTable(
                name: "ClassificationRules");

            migrationBuilder.DropTable(
                name: "AIRecommendations");

            migrationBuilder.DropTable(
                name: "JobFailures");

            migrationBuilder.DropTable(
                name: "ErrorTypes");

            migrationBuilder.DropTable(
                name: "MonitoredJobs");

            migrationBuilder.DropTable(
                name: "JobTypes");
        }
    }
}
