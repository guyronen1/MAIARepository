using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

public sealed class SqlClassificationRuleRepository(IDbContextFactory<MaiaDbContext> factory)
    : IClassificationRuleRepository
{
    public async Task<List<ClassificationRule>> GetByJobTypeAsync(
        int jobTypeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ClassificationRules
            .Include(r => r.ErrorType)
            .Include(r => r.JobType)
            .Where(r => r.JobTypeId == jobTypeId && r.IsActive)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);
    }

    public async Task<List<ClassificationRule>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ClassificationRules
            .Include(r => r.ErrorType)
            .Include(r => r.JobType)
            .OrderBy(r => r.JobTypeId).ThenBy(r => r.Priority)
            .ToListAsync(ct);
    }

    public async Task<ClassificationRule> SaveAsync(ClassificationRule rule, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ClassificationRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task UpdateAsync(ClassificationRule rule, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ClassificationRules.Update(rule);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int ruleId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rule = await db.ClassificationRules
            .Include(r => r.MonitoredJobRules)
            .FirstOrDefaultAsync(r => r.RuleId == ruleId, ct);
        if (rule is null) return;
        // MonitoredJobRules FK is Restrict, so remove links before the rule.
        db.MonitoredJobRules.RemoveRange(rule.MonitoredJobRules);
        db.ClassificationRules.Remove(rule);
        await db.SaveChangesAsync(ct);
    }
}
