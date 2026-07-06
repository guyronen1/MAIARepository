using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Maia.Core.Results;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

/// <summary>
/// Reads fix entries from the FixPolicyRules table so operators can configure
/// fix actions at runtime without code changes.
/// </summary>
public sealed class SqlFixCatalogueRepository(IDbContextFactory<MaiaDbContext> factory)
    : IFixCatalogueRepository
{
    public async Task<FixCatalogueEntry?> GetEntryAsync(
        string errorTypeCode,
        int    jobTypeId,
        int?   monitoredJobId = null,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Mirrors SqlFixPolicyRepository's two-layer lookup: override
        // (MonitoredJob-scoped) wins over default (JobType-scoped). Same
        // priority must apply at suggestion-generation time so the rec's
        // frozen AutoFixAvailable snapshot reflects the policy that would
        // actually execute, not the JobType default.
        FixPolicyRule? rule = null;
        if (monitoredJobId.HasValue)
        {
            rule = await db.FixPolicyRules
                .Include(r => r.ErrorType)
                .Where(r => r.Enabled
                         && r.MonitoredJobId == monitoredJobId.Value
                         && r.ErrorType != null
                         && r.ErrorType.Code == errorTypeCode)
                .OrderByDescending(r => r.ActionTimestamp)
                .FirstOrDefaultAsync(ct);
        }

        rule ??= await db.FixPolicyRules
            .Include(r => r.ErrorType)
            .Where(r => r.Enabled
                     && r.JobTypeId      == jobTypeId
                     && r.MonitoredJobId == null
                     && r.ErrorType != null
                     && r.ErrorType.Code == errorTypeCode)
            .OrderByDescending(r => r.ActionTimestamp)
            .FirstOrDefaultAsync(ct);

        if (rule is null) return null;

        return new FixCatalogueEntry(
            rule.ActionToApply,
            rule.FixCategory,
            ConfidenceBoost: 0.0,
            rule.IsAutoHealEligible);
    }
}
