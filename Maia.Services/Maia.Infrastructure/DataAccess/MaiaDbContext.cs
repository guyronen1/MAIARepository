using Maia.Core.Entities;
using Maia.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess;

public class MaiaDbContext(DbContextOptions<MaiaDbContext> options) : DbContext(options)
{
    public DbSet<JobType>          JobTypes            => Set<JobType>();
    public DbSet<ErrorType>        ErrorTypes          => Set<ErrorType>();
    public DbSet<JobFailure>       JobFailures         => Set<JobFailure>();
    public DbSet<ClassificationRule> ClassificationRules => Set<ClassificationRule>();
    public DbSet<FixPolicyRule>    FixPolicyRules      => Set<FixPolicyRule>();
    public DbSet<FixPolicyRuleStep> FixPolicyRuleSteps => Set<FixPolicyRuleStep>();
    public DbSet<AiRecommendation> AIRecommendations   => Set<AiRecommendation>();
    public DbSet<OperatorAction>   OperatorActions     => Set<OperatorAction>();
    public DbSet<FixExecutionLog>  FixExecutionLogs    => Set<FixExecutionLog>();
    public DbSet<AuditLog>         AuditLogs           => Set<AuditLog>();
    public DbSet<MonitoredJob>        MonitoredJobs       => Set<MonitoredJob>();
    public DbSet<MonitoredJobRule>    MonitoredJobRules   => Set<MonitoredJobRule>();
    public DbSet<ScanTypeDefinition>  ScanTypes           => Set<ScanTypeDefinition>();
    public DbSet<ScanSource>          ScanSources         => Set<ScanSource>();
    public DbSet<ScanCheckRule>       ScanCheckRules      => Set<ScanCheckRule>();
    public DbSet<ScanFileWatermark>   ScanFileWatermarks  => Set<ScanFileWatermark>();
    public DbSet<ScanContentWatermark> ScanContentWatermarks => Set<ScanContentWatermark>();
    public DbSet<ScanDbWatermark>     ScanDbWatermarks    => Set<ScanDbWatermark>();
    public DbSet<MonitoredJobLease>   MonitoredJobLeases  => Set<MonitoredJobLease>();
    public DbSet<ScanRunHistory>      ScanRunHistory      => Set<ScanRunHistory>();
    public DbSet<Role>                Roles               => Set<Role>();
    public DbSet<User>                Users               => Set<User>();
    public DbSet<UserSession>         UserSessions        => Set<UserSession>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        ConfigureJobType(mb);
        ConfigureErrorType(mb);
        ConfigureJobFailure(mb);
        ConfigureClassificationRule(mb);
        ConfigureFixPolicyRule(mb);
        ConfigureFixPolicyRuleStep(mb);
        ConfigureAiRecommendation(mb);
        ConfigureOperatorAction(mb);
        ConfigureFixExecutionLog(mb);
        ConfigureAuditLog(mb);
        ConfigureScanTypeDefinition(mb);
        ConfigureScanSource(mb);
        ConfigureScanCheckRule(mb);
        ConfigureScanFileWatermark(mb);
        ConfigureScanContentWatermark(mb);
        ConfigureScanDbWatermark(mb);
        ConfigureMonitoredJob(mb);
        ConfigureMonitoredJobRule(mb);
        ConfigureMonitoredJobLease(mb);
        ConfigureScanRunHistory(mb);
        ConfigureRole(mb);
        ConfigureUser(mb);
        ConfigureUserSession(mb);
        SeedData(mb);
    }

    // ── Table configurations ─────────────────────────────────────────────────

    private static void ConfigureJobType(ModelBuilder mb)
    {
        mb.Entity<JobType>(e =>
        {
            e.ToTable("JobTypes");
            e.HasKey(t => t.JobTypeId);
            e.Property(t => t.Name).IsRequired().HasMaxLength(100);
            e.Property(t => t.Description).HasMaxLength(500);
            e.Property(t => t.IsActive).HasDefaultValue(true);
        });
    }

    private static void ConfigureErrorType(ModelBuilder mb)
    {
        mb.Entity<ErrorType>(e =>
        {
            e.ToTable("ErrorTypes");
            e.HasKey(t => t.ErrorTypeId);
            e.HasIndex(t => t.Code).IsUnique();
            e.Property(t => t.Code).IsRequired().HasMaxLength(50);
            e.Property(t => t.DisplayName).IsRequired().HasMaxLength(100);
            e.Property(t => t.Description).HasMaxLength(500);
            e.Property(t => t.Severity).IsRequired().HasMaxLength(20).HasConversion<string>();
            e.Property(t => t.IsActive).HasDefaultValue(true);
        });
    }

    private static void ConfigureJobFailure(ModelBuilder mb)
    {
        mb.Entity<JobFailure>(e =>
        {
            e.ToTable("JobFailures");
            e.HasKey(j => j.FailureId);
            e.Property(j => j.StepName).HasMaxLength(200);
            e.Property(j => j.SourceId).HasMaxLength(500);
            e.Property(j => j.ErrorMessage).HasColumnType("nvarchar(max)");
            e.Property(j => j.DetectedAt).IsRequired().HasDefaultValueSql("GETDATE()");
            e.Property(j => j.SourceLogPath).IsRequired().HasMaxLength(200);
            e.Property(j => j.SourceFilePath).HasMaxLength(500);
            e.Property(j => j.ReferenceId).HasMaxLength(500);
            e.Property(j => j.Status).IsRequired().HasMaxLength(50).HasConversion<string>();

            e.HasOne(j => j.JobType)
                .WithMany(t => t.JobFailures)
                .HasForeignKey(j => j.JobTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(j => j.ErrorType)
                .WithMany(t => t.JobFailures)
                .HasForeignKey(j => j.ErrorTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(j => j.MonitoredJob)
                .WithMany(m => m.Failures)
                .HasForeignKey(j => j.MonitoredJobId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(j => j.ScanSource)
                .WithMany()
                .HasForeignKey(j => j.ScanSourceId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }

    private static void ConfigureClassificationRule(ModelBuilder mb)
    {
        mb.Entity<ClassificationRule>(e =>
        {
            e.ToTable("ClassificationRules");
            e.HasKey(r => r.RuleId);
            e.Property(r => r.Pattern).IsRequired().HasMaxLength(500);
            e.Property(r => r.Confidence).IsRequired().HasPrecision(5, 2);
            e.Property(r => r.Priority).IsRequired();
            e.Property(r => r.IsActive).HasDefaultValue(true);
            e.Property(r => r.CreatedBy).HasMaxLength(100);
            e.Property(r => r.SuggestedBy).HasMaxLength(50);
            e.Property(r => r.SuggestedFromHash).HasMaxLength(64);
            e.Property(r => r.SuggestedConfidence).HasPrecision(5, 2);

            // At most one ENABLED rule per (JobType, Pattern). Filtered so
            // disabled rows can duplicate freely (staged replacement) — mirrors
            // the FixPolicyRules active-key indexes. Case-insensitive collation
            // means "Error"/"error" collide, matching the classifier's
            // case-insensitive matching. Floor of the 3-layer duplicate guard.
            e.HasIndex(r => new { r.JobTypeId, r.Pattern })
                .HasFilter("[IsActive] = 1")
                .IsUnique()
                .HasDatabaseName("UX_ClassificationRules_ActiveKey");

            e.HasOne(r => r.JobType)
                .WithMany(jt => jt.ClassificationRules)
                .HasForeignKey(r => r.JobTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(r => r.ErrorType)
                .WithMany(et => et.ClassificationRules)
                .HasForeignKey(r => r.ErrorTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureFixPolicyRule(ModelBuilder mb)
    {
        mb.Entity<FixPolicyRule>(e =>
        {
            e.ToTable("FixPolicyRules");
            e.HasKey(r => r.RuleId);
            e.Property(r => r.ActionToApply).IsRequired().HasMaxLength(300);
            e.Property(r => r.FixCategory).IsRequired().HasMaxLength(50).HasConversion<string>();
            e.Property(r => r.IsAutoHealEligible).HasDefaultValue(false);
            e.Property(r => r.Enabled).HasDefaultValue(true);
            e.Property(r => r.CreatedBy).HasMaxLength(100);
            e.Property(r => r.SuggestedBy).HasMaxLength(50);
            e.Property(r => r.SuggestedFromHash).HasMaxLength(64);
            e.Property(r => r.SuggestedConfidence).HasPrecision(5, 2);
            e.Property(r => r.ActionTimestamp).HasDefaultValueSql("GETDATE()");
            e.Property(r => r.ActionType).IsRequired().HasMaxLength(50).HasConversion<string>().HasDefaultValue(FixActionType.Manual);
            e.Property(r => r.ActionPayload).HasColumnType("nvarchar(max)");

            e.HasOne(r => r.JobType)
                .WithMany(jt => jt.FixPolicyRules)
                .HasForeignKey(r => r.JobTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(r => r.ErrorType)
                .WithMany(et => et.FixPolicyRules)
                .HasForeignKey(r => r.ErrorTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // Optional override scope. NULL = JobType-level default (current
            // semantics for existing rows); non-NULL = per-MonitoredJob override
            // that wins over the default for that one job.
            // OnDelete(Restrict) is defensive against future hard-deletes —
            // today MonitoredJob.DeleteAsync is a soft-delete (sets IsActive=
            // false) so this FK never fires in practice. If a hard-delete is
            // ever introduced, the restriction surfaces a SQL error before any
            // operator's override gets silently erased; controller layer can
            // translate to a clean 409 at that point.
            e.HasOne(r => r.MonitoredJob)
                .WithMany()
                .HasForeignKey(r => r.MonitoredJobId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);

            // Two parallel filtered unique indexes — one per layer.
            //   Defaults: at most one enabled (JobType, ErrorType) pair when
            //             MonitoredJobId IS NULL.
            //   Overrides: at most one enabled (MonitoredJob, ErrorType) pair
            //              when MonitoredJobId IS NOT NULL.
            // A default and an override for the same (JobType, ErrorType) are
            // NOT duplicates — they're complementary by design. The earlier
            // single index UX_FixPolicyRules_ActiveKey is replaced by this
            // two-index pattern in the migration; existing rows (all with
            // MonitoredJobId=NULL) carry over cleanly under the Default index.
            e.HasIndex(r => new { r.JobTypeId, r.ErrorTypeId })
                .HasDatabaseName("UX_FixPolicyRules_DefaultActiveKey")
                .IsUnique()
                .HasFilter("[Enabled] = 1 AND [MonitoredJobId] IS NULL");

            e.HasIndex(r => new { r.MonitoredJobId, r.ErrorTypeId })
                .HasDatabaseName("UX_FixPolicyRules_OverrideActiveKey")
                .IsUnique()
                .HasFilter("[Enabled] = 1 AND [MonitoredJobId] IS NOT NULL");
        });
    }

    private static void ConfigureFixPolicyRuleStep(ModelBuilder mb)
    {
        mb.Entity<FixPolicyRuleStep>(e =>
        {
            e.ToTable("FixPolicyRuleSteps");
            e.HasKey(s => s.StepId);
            e.Property(s => s.ActionType).IsRequired().HasMaxLength(50).HasConversion<string>();
            e.Property(s => s.ActionPayload).IsRequired().HasColumnType("nvarchar(max)");
            e.Property(s => s.Description).HasMaxLength(200);

            // Cascade is intentional here — unlike FixPolicyRule.MonitoredJobId
            // which uses Restrict (overrides outlive jobs), steps cannot exist
            // without their parent rule. Deleting the rule obliterates steps.
            e.HasOne(s => s.Rule)
                .WithMany(r => r.Steps)
                .HasForeignKey(s => s.RuleId)
                .OnDelete(DeleteBehavior.Cascade);

            // (RuleId, StepOrder) is unique — every step has a distinct order
            // within its rule. Not filtered: even disabled rules need ordered
            // steps for editing. Controller normalises gaps to 1..N before
            // persist so the operator never trips on this.
            e.HasIndex(s => new { s.RuleId, s.StepOrder })
                .HasDatabaseName("UX_FixPolicyRuleSteps_RuleId_StepOrder")
                .IsUnique();
        });
    }

    private static void ConfigureAiRecommendation(ModelBuilder mb)
    {
        mb.Entity<AiRecommendation>(e =>
        {
            e.ToTable("AIRecommendations");
            e.HasKey(r => r.RecommendationId);
            e.Property(r => r.SuggestedAction).IsRequired().HasMaxLength(500);
            e.Property(r => r.FixCategory).IsRequired().HasMaxLength(50).HasConversion<string>();
            e.Property(r => r.ConfidenceScore).IsRequired().HasPrecision(5, 2);
            e.Property(r => r.Explanation).HasColumnType("nvarchar(max)");
            e.Property(r => r.RecommendedAt).IsRequired().HasDefaultValueSql("GETDATE()");
            e.Property(r => r.AutoFixAvailable).HasDefaultValue(false);
            e.Property(r => r.IsExecuted).HasDefaultValue(false);
            e.Property(r => r.ClaimedBy).HasMaxLength(200);
            // ClaimedAt is plain datetime2; no default — null means "unclaimed".

            // Partial index supports the atomic claim query — small (most rows
            // are IsExecuted=1 historical data, excluded here) and gives the
            // claim UPDATE a fast scan. Filter is intentionally NOT keyed on
            // OperatorApproved/AutoFixAvailable because either branch is
            // valid for claiming; the UPDATE's WHERE clause adds those.
            e.HasIndex(r => new { r.IsExecuted, r.ClaimedAt })
                .HasDatabaseName("IX_AIRecommendations_ClaimEligible")
                .HasFilter("[IsExecuted] = 0");

            e.HasOne(r => r.Failure)
                .WithMany(f => f.Recommendations)
                .HasForeignKey(r => r.FailureId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.ErrorType)
                .WithMany(et => et.Recommendations)
                .HasForeignKey(r => r.ErrorTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureOperatorAction(ModelBuilder mb)
    {
        mb.Entity<OperatorAction>(e =>
        {
            e.ToTable("OperatorActions");
            e.HasKey(o => o.ActionId);
            e.Property(o => o.OperatorId).IsRequired().HasMaxLength(100);
            e.Property(o => o.ActionTaken).IsRequired().HasMaxLength(200);
            e.Property(o => o.ActionTimestamp).IsRequired().HasDefaultValueSql("GETDATE()");

            e.HasOne(o => o.Recommendation)
                .WithMany(r => r.OperatorActions)
                .HasForeignKey(o => o.RecommendationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureFixExecutionLog(ModelBuilder mb)
    {
        mb.Entity<FixExecutionLog>(e =>
        {
            e.ToTable("FixExecutionLog");
            e.HasKey(f => f.FixId);
            e.Property(f => f.ExecutedAction).IsRequired().HasMaxLength(300);
            e.Property(f => f.TriggerType).IsRequired().HasMaxLength(50).HasConversion<string>();
            e.Property(f => f.ExecutedBy).IsRequired().HasMaxLength(100);
            e.Property(f => f.ExecutedAt).IsRequired().HasDefaultValueSql("GETDATE()");
            e.Property(f => f.ResultDetail).HasColumnType("nvarchar(max)");

            e.HasOne(f => f.Failure)
                .WithMany(j => j.FixExecutionLogs)
                .HasForeignKey(f => f.FailureId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(f => f.Recommendation)
                .WithMany()
                .HasForeignKey(f => f.RecommendationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAuditLog(ModelBuilder mb)
    {
        mb.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(a => a.AuditId);
            // EntityType / EntityId discriminate config audits from
            // failure-scoped ones. Both nullable for backward compatibility
            // with legacy rows; new writes always populate them.
            e.Property(a => a.EntityType).HasMaxLength(100);
            e.Property(a => a.EntityId).HasMaxLength(100);
            e.Property(a => a.EventType).IsRequired().HasMaxLength(100);
            e.Property(a => a.Actor).IsRequired().HasMaxLength(100);
            e.Property(a => a.Detail).HasColumnType("nvarchar(max)");
            e.Property(a => a.Timestamp).IsRequired().HasDefaultValueSql("GETDATE()");

            // FailureId is now nullable (int?) so config audits (which have
            // no associated JobFailure) fit the same table. Cascade still
            // fires when a JobFailure is deleted — but only for rows that
            // actually reference one.
            e.HasOne(a => a.Failure)
                .WithMany(j => j.AuditLogs)
                .HasForeignKey(a => a.FailureId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureScanTypeDefinition(ModelBuilder mb)
    {
        mb.Entity<ScanTypeDefinition>(e =>
        {
            e.ToTable("ScanTypes");
            e.HasKey(s => s.ScanTypeId);
            e.Property(s => s.Name).IsRequired().HasMaxLength(100);
            e.Property(s => s.Description).HasMaxLength(500);
            e.Property(s => s.LeaseDurationSeconds).IsRequired().HasDefaultValue(300);
        });
    }

    private static void ConfigureScanSource(ModelBuilder mb)
    {
        mb.Entity<ScanSource>(e =>
        {
            e.ToTable("ScanSources");
            e.HasKey(s => s.ScanSourceId);
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
            e.Property(s => s.LogFolder).HasMaxLength(500);
            e.Property(s => s.SearchPatterns).HasMaxLength(500);
            e.Property(s => s.InputFolder).HasMaxLength(500);
            e.Property(s => s.IncludeSubfolders).IsRequired().HasDefaultValue(false);
            e.Property(s => s.ConnectionName).HasMaxLength(200);
            e.Property(s => s.LogSourceUrl).HasMaxLength(500);
            e.Property(s => s.PollingIntervalSeconds).HasDefaultValue(300);
            e.Property(s => s.IsActive).HasDefaultValue(true);

            e.HasOne(s => s.MonitoredJob)
                .WithMany(m => m.ScanSources)
                .HasForeignKey(s => s.MonitoredJobId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(s => s.ScanTypeDefinition)
                .WithMany()
                .HasForeignKey(s => s.ScanTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(s => s.MonitoredJobId).HasDatabaseName("IX_ScanSources_MonitoredJobId");
        });
    }

    private static void ConfigureScanCheckRule(ModelBuilder mb)
    {
        mb.Entity<ScanCheckRule>(e =>
        {
            e.ToTable("ScanCheckRules");
            e.HasKey(r => r.CheckRuleId);
            e.Property(r => r.CheckType).IsRequired().HasMaxLength(50).HasConversion<string>();
            // nvarchar(max): for ColumnRange/ValueEquals this holds a table name
            // ("dbo.Orders"); for CheckType.SqlQuery it holds the operator-written
            // query / EXEC statement, which can be multi-line and well over 200 chars.
            e.Property(r => r.SourceTable).HasColumnType("nvarchar(max)");
            e.Property(r => r.TargetField).IsRequired().HasMaxLength(200);
            e.Property(r => r.MinValue).HasPrecision(18, 4);
            e.Property(r => r.MaxValue).HasPrecision(18, 4);
            e.Property(r => r.ExpectedValue).HasMaxLength(500);
            e.Property(r => r.WatermarkColumn).HasMaxLength(200);
            e.Property(r => r.SourceIdColumn).HasMaxLength(200);
            e.Property(r => r.ReferenceIdColumn).HasMaxLength(200);
            e.Property(r => r.FilePathColumn).HasMaxLength(100);
            e.Property(r => r.InputPathPattern).HasMaxLength(500);
            // FileContent scan fields — all nullable (NULL on FS/DB/API rules).
            // Enums stored as their string name, matching every other enum column.
            e.Property(r => r.ExtractorType).HasMaxLength(50).HasConversion<string>();
            e.Property(r => r.ExtractorLocator).HasMaxLength(500);
            e.Property(r => r.IdentifierLocator).HasMaxLength(500);
            e.Property(r => r.ExtractorPredicateType).HasMaxLength(50).HasConversion<string>();
            e.Property(r => r.ExtractorPredicateValue).HasMaxLength(500);
            e.Property(r => r.Severity).IsRequired().HasMaxLength(20).HasConversion<string>();
            e.Property(r => r.Description).HasMaxLength(500);
            e.Property(r => r.IsActive).HasDefaultValue(true);

            e.HasOne(r => r.MonitoredJob)
                .WithMany(m => m.ScanCheckRules)
                .HasForeignKey(r => r.MonitoredJobId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tier 2.5 nullable FK → ScanSource. NoAction: the row is already
            // cascade-deleted via MonitoredJob, so a 2nd cascade path here would
            // trip SQL Server's multiple-cascade-paths check.
            e.HasOne(r => r.ScanSource)
                .WithMany(s => s.ScanCheckRules)
                .HasForeignKey(r => r.ScanSourceId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }

    private static void ConfigureScanDbWatermark(ModelBuilder mb)
    {
        mb.Entity<ScanDbWatermark>(e =>
        {
            e.ToTable("ScanDbWatermarks");
            e.HasKey(w => w.WatermarkId);
            e.HasIndex(w => w.CheckRuleId).IsUnique();
            e.Property(w => w.WatermarkValue).IsRequired().HasMaxLength(100);
            e.Property(w => w.LastScannedAt).IsRequired().HasDefaultValueSql("GETDATE()");

            e.HasOne(w => w.CheckRule)
                .WithMany()
                .HasForeignKey(w => w.CheckRuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureScanFileWatermark(ModelBuilder mb)
    {
        mb.Entity<ScanFileWatermark>(e =>
        {
            e.ToTable("ScanFileWatermarks");
            e.HasKey(w => w.WatermarkId);
            e.HasIndex(w => new { w.MonitoredJobId, w.FilePath }).IsUnique();
            e.Property(w => w.FilePath).IsRequired().HasMaxLength(500);
            e.Property(w => w.ByteOffset).IsRequired();
            e.Property(w => w.LastScannedAt).IsRequired().HasDefaultValueSql("GETDATE()");

            e.HasOne(w => w.MonitoredJob)
                .WithMany()
                .HasForeignKey(w => w.MonitoredJobId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(w => w.ScanSource)
                .WithMany()
                .HasForeignKey(w => w.ScanSourceId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }

    private static void ConfigureScanContentWatermark(ModelBuilder mb)
    {
        mb.Entity<ScanContentWatermark>(e =>
        {
            e.ToTable("ScanContentWatermarks");
            e.HasKey(w => w.WatermarkId);
            e.HasIndex(w => new { w.MonitoredJobId, w.FilePath }).IsUnique();
            e.Property(w => w.FilePath).IsRequired().HasMaxLength(500);
            e.Property(w => w.LastScannedAt).IsRequired().HasDefaultValueSql("GETDATE()");
            e.Property(w => w.LastModifiedAt).IsRequired();

            e.HasOne(w => w.MonitoredJob)
                .WithMany()
                .HasForeignKey(w => w.MonitoredJobId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(w => w.ScanSource)
                .WithMany()
                .HasForeignKey(w => w.ScanSourceId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }

    private static void ConfigureMonitoredJob(ModelBuilder mb)
    {
        mb.Entity<MonitoredJob>(e =>
        {
            e.ToTable("MonitoredJobs");
            e.HasKey(m => m.MonitoredJobId);
            e.HasIndex(m => m.Name).IsUnique();
            e.Property(m => m.Name).IsRequired().HasMaxLength(200);
            e.Property(m => m.DisplayName).HasMaxLength(300);
            e.Property(m => m.PollingIntervalSeconds).HasDefaultValue(300);
            e.Property(m => m.IsActive).HasDefaultValue(true);
            e.Property(m => m.Description).HasColumnType("nvarchar(1000)");
            e.Property(m => m.CreatedAt).IsRequired().HasDefaultValueSql("GETDATE()");

            e.HasOne(m => m.JobType)
                .WithMany(jt => jt.MonitoredJobs)
                .HasForeignKey(m => m.JobTypeId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureMonitoredJobLease(ModelBuilder mb)
    {
        mb.Entity<MonitoredJobLease>(e =>
        {
            e.ToTable("MonitoredJobLeases");
            e.HasKey(l => l.MonitoredJobId);
            e.Property(l => l.MonitoredJobId).ValueGeneratedNever();
            e.Property(l => l.LeasedBy).HasMaxLength(200);
            e.Property(l => l.LeasedAt).HasColumnType("datetime2(3)");
            e.Property(l => l.LeasedUntil).HasColumnType("datetime2(3)");
            e.Property(l => l.NextEligibleAt).HasColumnType("datetime2(3)").IsRequired().HasDefaultValueSql("'0001-01-01'");
            e.Property(l => l.LastRunStartedAt).HasColumnType("datetime2(3)");
            e.Property(l => l.LastRunCompletedAt).HasColumnType("datetime2(3)");
            e.Property(l => l.LastRunOutcome).HasConversion<string>().HasMaxLength(50);
            e.Property(l => l.LastRunError).HasMaxLength(2000);

            e.HasIndex(l => new { l.NextEligibleAt, l.LeasedUntil })
                .HasDatabaseName("IX_MonitoredJobLeases_Eligible");

            e.HasOne(l => l.MonitoredJob)
                .WithOne(m => m.Lease)
                .HasForeignKey<MonitoredJobLease>(l => l.MonitoredJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureMonitoredJobRule(ModelBuilder mb)
    {
        mb.Entity<MonitoredJobRule>(e =>
        {
            e.ToTable("MonitoredJobRules");
            e.HasKey(r => r.JobRuleId);
            e.HasIndex(r => new { r.MonitoredJobId, r.RuleId }).IsUnique();
            e.Property(r => r.IsActive).HasDefaultValue(true);

            e.HasOne(r => r.MonitoredJob)
                .WithMany(m => m.JobRules)
                .HasForeignKey(r => r.MonitoredJobId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.Rule)
                .WithMany(cr => cr.MonitoredJobRules)
                .HasForeignKey(r => r.RuleId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureScanRunHistory(ModelBuilder mb)
    {
        mb.Entity<ScanRunHistory>(e =>
        {
            e.ToTable("ScanRunHistory");
            e.HasKey(r => r.ScanRunId);                                  // PK → clustered (default)
            e.Property(r => r.LeasedBy).IsRequired().HasMaxLength(200);
            e.Property(r => r.StartedAt).HasColumnType("datetime2(3)").IsRequired();
            e.Property(r => r.CompletedAt).HasColumnType("datetime2(3)").IsRequired();
            e.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(50).IsRequired();
            e.Property(r => r.Error).HasMaxLength(2000);
            e.Property(r => r.IdentifierExtractionFailures).IsRequired().HasDefaultValue(0);
            e.Property(r => r.OversizeFileSkips).IsRequired().HasDefaultValue(0);
            e.Property(r => r.PredicateUnevaluableSkips).IsRequired().HasDefaultValue(0);

            e.HasOne(r => r.MonitoredJob)
                .WithMany()
                .HasForeignKey(r => r.MonitoredJobId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.ScanSource)
                .WithMany()
                .HasForeignKey(r => r.ScanSourceId)
                .OnDelete(DeleteBehavior.NoAction);

            // Per-source "last scan" lookups (drill-down) once the worker runs
            // per-source. Complements the per-job index below.
            e.HasIndex(r => new { r.ScanSourceId, r.StartedAt })
                .HasDatabaseName("IX_ScanRunHistory_Source_StartedAt")
                .IsDescending(false, true);

            // Covers "last N runs of job X" — no bookmark lookups for the columns the
            // /scan-runs endpoint surfaces.
            e.HasIndex(r => new { r.MonitoredJobId, r.StartedAt })
                .HasDatabaseName("IX_ScanRunHistory_Job_StartedAt")
                .IsDescending(false, true)
                .IncludeProperties(r => new
                {
                    r.CompletedAt,
                    r.DurationMs,
                    r.Outcome,
                    r.FailuresDetected,
                    r.Classifications,
                    r.Recommendations,
                });

            // Filtered: "recent failures across all jobs" — tiny (most rows are Success).
            // Outcome is stored as a string (HasConversion<string>), so the filter compares
            // the persisted string form, not the enum's int value.
            e.HasIndex(r => r.StartedAt)
                .HasDatabaseName("IX_ScanRunHistory_Failures")
                .IsDescending(true)
                .IncludeProperties(r => new { r.MonitoredJobId, r.Outcome, r.Error })
                .HasFilter("[Outcome] <> 'Success'");
        });
    }

    private static void ConfigureRole(ModelBuilder mb)
    {
        mb.Entity<Role>(e =>
        {
            e.ToTable("Roles");
            e.HasKey(r => r.RoleId);
            // RoleId is aligned to MaiaRole and seeded explicitly, so it must not
            // be store-generated.
            e.Property(r => r.RoleId).ValueGeneratedNever();
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.Name).IsRequired().HasMaxLength(50);
        });
    }

    private static void ConfigureUser(ModelBuilder mb)
    {
        mb.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(u => u.UserId);
            // Unique-CI on Username via the DB's default collation (matches the
            // case-insensitive lookup in SqlUserRepository).
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Username).IsRequired().HasMaxLength(100);
            // Identity's PBKDF2 v3 string is ~84 chars; 500 leaves ample headroom
            // for a future format/iteration bump.
            e.Property(u => u.PasswordHash).IsRequired().HasMaxLength(500);
            e.Property(u => u.IsActive).HasDefaultValue(true);
            e.Property(u => u.MustChangePassword).HasDefaultValue(false);
            e.Property(u => u.CreatedAt).IsRequired().HasDefaultValueSql("GETDATE()");

            e.HasOne(u => u.Role)
                .WithMany(r => r.Users)
                .HasForeignKey(u => u.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureUserSession(ModelBuilder mb)
    {
        mb.Entity<UserSession>(e =>
        {
            e.ToTable("UserSessions");
            e.HasKey(s => s.SessionId);
            e.HasIndex(s => s.Token).IsUnique();
            e.Property(s => s.Token).IsRequired().HasMaxLength(200);
            e.Property(s => s.CreatedAt).IsRequired().HasDefaultValueSql("GETDATE()");
            e.Property(s => s.LastActivityAt).IsRequired().HasDefaultValueSql("GETDATE()");

            e.HasOne(s => s.User)
                .WithMany(u => u.Sessions)
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    // ── Seed data ────────────────────────────────────────────────────────────

    private static void SeedData(ModelBuilder mb)
    {
        // Roles are a fixed set; RoleId == (int)MaiaRole. The bootstrap admin USER
        // is NOT seeded here — its PBKDF2 hash uses a random salt (non-deterministic),
        // which would break HasData drift detection. It's inserted via raw SQL in the
        // AddAuthTables migration instead.
        mb.Entity<Role>().HasData(
            new Role { RoleId = (int)MaiaRole.User,          Name = "User" },
            new Role { RoleId = (int)MaiaRole.Operator,      Name = "Operator" },
            new Role { RoleId = (int)MaiaRole.Administrator, Name = "Administrator" }
        );

        mb.Entity<JobType>().HasData(
            new JobType { JobTypeId = 1, Name = "DTSX",        Description = "SQL Server Integration Services package", IsActive = true },
            new JobType { JobTypeId = 2, Name = "SqlAgent",    Description = "SQL Server Agent Job",                   IsActive = true },
            new JobType { JobTypeId = 3, Name = "Python",      Description = "Python script",                          IsActive = true },
            new JobType { JobTypeId = 4, Name = "PowerShell",  Description = "PowerShell script",                      IsActive = true },
            new JobType { JobTypeId = 5, Name = "Exe",         Description = "Native executable / batch process",      IsActive = true }
        );

        mb.Entity<ErrorType>().HasData(
            new ErrorType { ErrorTypeId = 1, Code = "FileNotFound", DisplayName = "File Not Found",           Severity = Severity.High,   IsActive = true },
            new ErrorType { ErrorTypeId = 2, Code = "DbConnection", DisplayName = "Database Connection Error",Severity = Severity.High,   IsActive = true },
            new ErrorType { ErrorTypeId = 3, Code = "Timeout",      DisplayName = "Execution Timeout",        Severity = Severity.Medium, IsActive = true },
            new ErrorType { ErrorTypeId = 4, Code = "Transform",    DisplayName = "Data Transform Failure",   Severity = Severity.Medium, IsActive = true },
            new ErrorType { ErrorTypeId = 5, Code = "Permission",   DisplayName = "Access Denied",            Severity = Severity.High,   IsActive = true },
            new ErrorType { ErrorTypeId = 6, Code = "Unknown",      DisplayName = "Unknown Error",            Severity = Severity.Low,    IsActive = true }
        );

        mb.Entity<ScanTypeDefinition>().HasData(
            new ScanTypeDefinition { ScanTypeId = 1, Name = "FileSystem",  Description = "Scan log files in a folder matching glob patterns",          LeaseDurationSeconds = 300  },
            new ScanTypeDefinition { ScanTypeId = 2, Name = "Database",    Description = "Query a SQL table and check column values against rules",   LeaseDurationSeconds = 1800 },
            new ScanTypeDefinition { ScanTypeId = 3, Name = "ApiEndpoint", Description = "Poll an HTTP endpoint and inspect the response",            LeaseDurationSeconds = 60   },
            new ScanTypeDefinition { ScanTypeId = 4, Name = "FileContent", Description = "Structured extraction from input data files (XML, …)",     LeaseDurationSeconds = 300  }
        );

        // NOTE: only environment-agnostic reference metadata is seeded here
        // (Roles, JobTypes, ErrorTypes, ScanTypes). Demo/job data — the
        // TrapInterfaces MonitoredJob, its rules/lease, the dev ClassificationRules,
        // and the dev FixPolicyRule — used to live here but were removed: they are
        // not part of any real deployment. Production job config is applied via the
        // SQL seed scripts (Scripts/seed-metadata.sql + Scripts/seed-nsg-events.sql).
    }
}
