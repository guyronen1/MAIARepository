using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlMonitoredJobRepository(IDbContextFactory<MaiaDbContext> factory)
    : IMonitoredJobRepository
{
    public async Task<List<MonitoredJob>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.ScanCheckRules.Where(r => r.IsActive))
            .Include(m => m.ScanSources.Where(s => s.IsActive))
                .ThenInclude(s => s.ScanTypeDefinition)
            .Include(m => m.ScanSources.Where(s => s.IsActive))
                .ThenInclude(s => s.ScanCheckRules.Where(r => r.IsActive))
            .Where(m => m.IsActive)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    public async Task<List<MonitoredJob>> GetActiveWithRulesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.Lease)
            .Include(m => m.ScanCheckRules.Where(r => r.IsActive))
            .Include(m => m.JobRules.Where(jr => jr.IsActive))
                .ThenInclude(jr => jr.Rule)
                    .ThenInclude(r => r!.ErrorType)
            .Where(m => m.IsActive)
            .ToListAsync(ct);
    }

    public async Task<MonitoredJob?> GetByIdAsync(int monitoredJobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.ScanCheckRules.Where(r => r.IsActive))
            .Include(m => m.JobRules).ThenInclude(jr => jr.Rule).ThenInclude(r => r!.ErrorType)
            .Include(m => m.ScanSources.Where(s => s.IsActive))
                .ThenInclude(s => s.ScanTypeDefinition)
            .Include(m => m.ScanSources.Where(s => s.IsActive))
                .ThenInclude(s => s.ScanCheckRules.Where(r => r.IsActive))
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.MonitoredJobId == monitoredJobId, ct);
    }

    public async Task<MonitoredJob?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.ScanCheckRules.Where(r => r.IsActive))
            .Include(m => m.ScanSources.Where(s => s.IsActive))
                .ThenInclude(s => s.ScanTypeDefinition)
            .Include(m => m.ScanSources.Where(s => s.IsActive))
                .ThenInclude(s => s.ScanCheckRules.Where(r => r.IsActive))
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Name == name, ct);
    }

    public async Task<List<ClassificationRule>> GetEffectiveRulesAsync(
        int monitoredJobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var job = await db.MonitoredJobs
            .Include(m => m.JobRules.Where(jr => jr.IsActive))
                .ThenInclude(jr => jr.Rule)
                    .ThenInclude(r => r!.ErrorType)
            .FirstOrDefaultAsync(m => m.MonitoredJobId == monitoredJobId, ct);

        if (job is null) return [];

        // UNION semantics (replaces the old "linked-only when any links exist").
        // A JobType-level classification rule applies to every job of that type;
        // per-job linked rules ADD on top. The classifier returns the first
        // matching rule in list order, so ordering linked rules FIRST gives
        // "linked beats global" (mirrors the FixPolicyRule override→default
        // precedence): a global only fires on a line no linked rule matched.
        var linked = job.JobRules
            .Select(jr => jr.Rule!)
            .Where(r => r is { IsActive: true })
            .OrderBy(r => r.Priority)
            .ToList();

        // JobType DEFAULTS = active rules of this JobType with NO active job
        // link. A rule linked to a specific job is THAT job's override and must
        // not leak to its siblings — so exclude any rule that has an active
        // MonitoredJobRules link (this job's own links are already in `linked`).
        var globals = await db.ClassificationRules
            .Include(r => r.ErrorType)
            .Where(r => r.JobTypeId == job.JobTypeId && r.IsActive
                     && !db.MonitoredJobRules.Any(m => m.RuleId == r.RuleId && m.IsActive))
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        // This job's overrides first (by Priority), then the JobType defaults.
        return linked.Concat(globals).ToList();
    }

    public async Task<List<MonitoredJob>> GetAllWithRulesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitoredJobs
            .Include(m => m.JobType)
            .Include(m => m.Lease)
            .Include(m => m.ScanCheckRules)
            .Include(m => m.JobRules.Where(jr => jr.IsActive))
                .ThenInclude(jr => jr.Rule).ThenInclude(r => r!.ErrorType)
            .Include(m => m.ScanSources.Where(s => s.IsActive))
                .ThenInclude(s => s.ScanTypeDefinition)
            .Include(m => m.ScanSources.Where(s => s.IsActive))
                .ThenInclude(s => s.ScanCheckRules.Where(r => r.IsActive))
            .OrderBy(m => m.Name)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    public async Task<MonitoredJob> SaveAsync(MonitoredJob job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.MonitoredJobs.Add(job);

        // 1:1 lease row created with the job — immediately eligible (NextEligibleAt = MinValue).
        job.Lease = new MonitoredJobLease { NextEligibleAt = DateTime.MinValue };

        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task UpdateAsync(MonitoredJob job, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.MonitoredJobs
            .Where(m => m.MonitoredJobId == job.MonitoredJobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Name,                   job.Name)
                .SetProperty(m => m.DisplayName,            job.DisplayName)
                .SetProperty(m => m.JobTypeId,              job.JobTypeId)
                .SetProperty(m => m.PollingIntervalSeconds, job.PollingIntervalSeconds)
                .SetProperty(m => m.IsActive,               job.IsActive)
                .SetProperty(m => m.Description,            job.Description),
            ct);
    }

    public async Task DeleteAsync(int monitoredJobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.MonitoredJobs.FindAsync([monitoredJobId], ct);
        if (job is null) return;
        job.IsActive = false;
        await db.SaveChangesAsync(ct);
    }
}
