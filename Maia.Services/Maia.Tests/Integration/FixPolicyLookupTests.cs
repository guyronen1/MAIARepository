using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.DataAccess.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maia.Tests.Integration;

/// <summary>
/// Covers the two-layer FixPolicyRule lookup contract — override
/// (MonitoredJob-scoped) wins over default (JobType-scoped). Same priority
/// rules apply at both execution time (IFixPolicyRepository) and suggestion-
/// generation time (IFixCatalogueRepository); both are exercised here so a
/// future change to one without the other is caught.
///
/// Trade-off: InMemory does not enforce the filtered-unique-index constraints
/// that the migration installs. Duplicate-prevention is verified at the
/// controller layer (manual 409 check + DB index); these tests focus on
/// "given a valid mix of defaults and overrides, which row wins."
/// </summary>
public class FixPolicyLookupTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;

    // IDs are picked above the MaiaDbContext seed-data ranges (HasData seeds
    // JobTypes 1-4, ErrorTypes 1-6, MonitoredJob 1, FixPolicyRule 1) to keep
    // each test starting from a clean policy table.
    private const int JobTypeId    = 101;
    private const int OtherJobType = 102;
    private const int ErrorTypeId  = 110;
    private const string ErrorCode = "FileSendFailed";
    private const int JobA = 1001;
    private const int JobB = 1002;
    private const int DefaultRuleId  = 1001;
    private const int OverrideRuleId = 1002;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db      = new MaiaDbContext(options);
        _factory = new TestDbContextFactory(options);
        await _db.Database.EnsureCreatedAsync();

        _db.JobTypes.AddRange(
            new JobType { JobTypeId = JobTypeId,    Name = "DTSX" },
            new JobType { JobTypeId = OtherJobType, Name = "Exe"  });
        _db.ErrorTypes.Add(new ErrorType
        {
            ErrorTypeId = ErrorTypeId, Code = ErrorCode,
            DisplayName = "File Send Failed", Severity = Severity.High,
        });
        _db.MonitoredJobs.AddRange(
            new MonitoredJob { MonitoredJobId = JobA, Name = "JobA", JobTypeId = JobTypeId },
            new MonitoredJob { MonitoredJobId = JobB, Name = "JobB", JobTypeId = JobTypeId });
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    // ─────────────────────────────────────────────────────────────────────────
    // IFixPolicyRepository — execution-time lookup
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetForAsync_OverrideWinsOverDefault_WhenBothExist()
    {
        var defaultRule  = MakeRule(DefaultRuleId,  monitoredJobId: null, action: "Default action");
        var overrideRule = MakeRule(OverrideRuleId, monitoredJobId: JobA, action: "Override for A");
        _db.FixPolicyRules.AddRange(defaultRule, overrideRule);
        await _db.SaveChangesAsync();

        var repo   = new SqlFixPolicyRepository(_factory);
        var picked = await repo.GetForAsync(JobTypeId, ErrorTypeId, JobA);

        Assert.NotNull(picked);
        Assert.Equal(OverrideRuleId, picked!.RuleId);
        Assert.Equal("Override for A", picked.ActionToApply);
    }

    [Fact]
    public async Task GetForAsync_FallsBackToDefault_WhenNoOverrideForThisJob()
    {
        var defaultRule = MakeRule(DefaultRuleId, monitoredJobId: null, action: "Default action");
        var overrideForOther =
            MakeRule(OverrideRuleId, monitoredJobId: JobB, action: "Override for B");
        _db.FixPolicyRules.AddRange(defaultRule, overrideForOther);
        await _db.SaveChangesAsync();

        var repo   = new SqlFixPolicyRepository(_factory);
        var picked = await repo.GetForAsync(JobTypeId, ErrorTypeId, JobA);

        Assert.NotNull(picked);
        Assert.Equal(DefaultRuleId, picked!.RuleId);
        Assert.Null(picked.MonitoredJobId);
    }

    [Fact]
    public async Task GetForAsync_FallsBackToDefault_WhenCallerOmitsMonitoredJobId()
    {
        _db.FixPolicyRules.Add(MakeRule(DefaultRuleId,  monitoredJobId: null, action: "Default"));
        _db.FixPolicyRules.Add(MakeRule(OverrideRuleId, monitoredJobId: JobA, action: "Override"));
        await _db.SaveChangesAsync();

        var repo = new SqlFixPolicyRepository(_factory);
        // No monitoredJobId → override layer is invisible by design.
        var picked = await repo.GetForAsync(JobTypeId, ErrorTypeId);

        Assert.NotNull(picked);
        Assert.Equal(DefaultRuleId, picked!.RuleId);
    }

    [Fact]
    public async Task GetForAsync_DisabledOverride_FallsThroughToDefault()
    {
        var defaultRule = MakeRule(DefaultRuleId,  monitoredJobId: null, action: "Default");
        var disabled    = MakeRule(OverrideRuleId, monitoredJobId: JobA, action: "Override (off)");
        disabled.Enabled = false;
        _db.FixPolicyRules.AddRange(defaultRule, disabled);
        await _db.SaveChangesAsync();

        var repo   = new SqlFixPolicyRepository(_factory);
        var picked = await repo.GetForAsync(JobTypeId, ErrorTypeId, JobA);

        Assert.NotNull(picked);
        Assert.Equal(DefaultRuleId, picked!.RuleId);
    }

    [Fact]
    public async Task GetForAsync_ReturnsNull_WhenNothingMatches()
    {
        _db.FixPolicyRules.Add(MakeRule(
            DefaultRuleId, monitoredJobId: null, action: "Wrong JobType",
            jobTypeId: OtherJobType));
        await _db.SaveChangesAsync();

        var repo   = new SqlFixPolicyRepository(_factory);
        var picked = await repo.GetForAsync(JobTypeId, ErrorTypeId, JobA);

        Assert.Null(picked);
    }

    [Fact]
    public async Task GetForAsync_OverrideIgnoresJobTypeId()
    {
        // Override is keyed on (MonitoredJobId, ErrorTypeId) only — the
        // JobTypeId column on an override row is informational/legacy. The
        // override must still win even if the caller's jobTypeId doesn't
        // match the override row's JobTypeId column.
        _db.FixPolicyRules.Add(MakeRule(DefaultRuleId,  monitoredJobId: null, action: "Default"));
        _db.FixPolicyRules.Add(MakeRule(
            OverrideRuleId, monitoredJobId: JobA, action: "Override",
            jobTypeId: OtherJobType));
        await _db.SaveChangesAsync();

        var repo   = new SqlFixPolicyRepository(_factory);
        var picked = await repo.GetForAsync(JobTypeId, ErrorTypeId, JobA);

        Assert.NotNull(picked);
        Assert.Equal(OverrideRuleId, picked!.RuleId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IFixCatalogueRepository — suggestion-generation lookup mirrors the same
    // priority. If these drift apart the rec's frozen AutoFixAvailable snapshot
    // would disagree with what the executor later runs.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FixCatalogueRepo_OverrideWins_AndExposesAutoHealFlag()
    {
        var defaultRule  = MakeRule(DefaultRuleId,  monitoredJobId: null, action: "Default");
        defaultRule.IsAutoHealEligible = false;
        var overrideRule = MakeRule(OverrideRuleId, monitoredJobId: JobA, action: "Override");
        overrideRule.IsAutoHealEligible = true;
        _db.FixPolicyRules.AddRange(defaultRule, overrideRule);
        await _db.SaveChangesAsync();

        var repo  = new SqlFixCatalogueRepository(_factory);
        var entry = await repo.GetEntryAsync(ErrorCode, JobTypeId, JobA);

        Assert.NotNull(entry);
        Assert.Equal("Override", entry!.SuggestedAction);
        Assert.True(entry.AutoHeal);
    }

    [Fact]
    public async Task FixCatalogueRepo_FallsBackToDefault_WhenNoOverride()
    {
        _db.FixPolicyRules.Add(MakeRule(DefaultRuleId, monitoredJobId: null, action: "Default"));
        await _db.SaveChangesAsync();

        var repo  = new SqlFixCatalogueRepository(_factory);
        var entry = await repo.GetEntryAsync(ErrorCode, JobTypeId, JobA);

        Assert.NotNull(entry);
        Assert.Equal("Default", entry!.SuggestedAction);
    }

    [Fact]
    public async Task GetForAsync_CompositeRule_LoadsStepsOrderedByStepOrder()
    {
        // Pins the .Include(r => r.Steps.OrderBy(s => s.StepOrder)) in
        // SqlFixPolicyRepository — without it, DefaultFixEngine's composite
        // branch sees an empty Steps collection and silently does nothing.
        // Also pins the order: steps must come back ascending regardless of
        // insertion order.
        var composite = new FixPolicyRule
        {
            RuleId          = DefaultRuleId,
            JobTypeId       = JobTypeId,
            ErrorTypeId     = ErrorTypeId,
            MonitoredJobId  = null,
            ActionToApply   = "Composite default",
            FixCategory     = FixCategory.Retry,
            ActionType      = FixActionType.Composite,
            ActionPayload   = null,
            Enabled         = true,
            ActionTimestamp = DateTime.Now,
            Steps           =
            [
                new FixPolicyRuleStep
                {
                    StepId        = 1,
                    StepOrder     = 3,
                    ActionType    = FixActionType.SqlScript,
                    ActionPayload = "third",
                },
                new FixPolicyRuleStep
                {
                    StepId        = 2,
                    StepOrder     = 1,
                    ActionType    = FixActionType.CopyFile,
                    ActionPayload = "first",
                },
                new FixPolicyRuleStep
                {
                    StepId        = 3,
                    StepOrder     = 2,
                    ActionType    = FixActionType.Script,
                    ActionPayload = "second",
                },
            ],
        };
        _db.FixPolicyRules.Add(composite);
        await _db.SaveChangesAsync();

        var repo   = new SqlFixPolicyRepository(_factory);
        var picked = await repo.GetForAsync(JobTypeId, ErrorTypeId);

        Assert.NotNull(picked);
        Assert.Equal(3, picked!.Steps.Count);
        Assert.Equal(new[] { 1, 2, 3 }, picked.Steps.Select(s => s.StepOrder).ToArray());
        Assert.Equal(new[] { "first", "second", "third" },
            picked.Steps.Select(s => s.ActionPayload).ToArray());
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static FixPolicyRule MakeRule(
        int ruleId, int? monitoredJobId, string action,
        int? jobTypeId = null) =>
        new()
        {
            RuleId         = ruleId,
            JobTypeId      = jobTypeId ?? JobTypeId,
            ErrorTypeId    = ErrorTypeId,
            MonitoredJobId = monitoredJobId,
            ActionToApply  = action,
            FixCategory    = FixCategory.Retry,
            ActionType     = FixActionType.Manual,
            Enabled        = true,
            ActionTimestamp = DateTime.Now,
        };

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
