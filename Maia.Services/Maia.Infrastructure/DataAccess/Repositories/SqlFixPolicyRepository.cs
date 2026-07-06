using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Maia.Infrastructure.DataAccess.Repositories;

/// <summary>
/// Loads the FixPolicyRule that applies for execution. Two-layer lookup —
/// override (MonitoredJob-scoped) wins over default (JobType-scoped).
/// </summary>
public sealed class SqlFixPolicyRepository(IDbContextFactory<MaiaDbContext> factory)
    : IFixPolicyRepository
{
    public async Task<FixPolicyRule?> GetForAsync(
        int jobTypeId,
        int errorTypeId,
        int? monitoredJobId = null,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // ── Try override layer first (only if caller supplied a job id) ──
        // The DB has a filtered unique index on (MonitoredJobId, ErrorTypeId)
        // WHERE Enabled = 1 AND MonitoredJobId IS NOT NULL — at most one row
        // can match, so the OrderBy here is just a defensive tiebreaker.
        // Steps include is split (AsSplitQuery) — without it, EF emits a
        // Cartesian join warning when both ErrorType (single) and Steps
        // (collection) are included on the same query.
        if (monitoredJobId.HasValue)
        {
            var overrideRule = await db.FixPolicyRules
                .Include(r => r.ErrorType)
                .Include(r => r.Steps.OrderBy(s => s.StepOrder))
                .AsSplitQuery()
                .Where(r => r.MonitoredJobId == monitoredJobId.Value
                         && r.ErrorTypeId    == errorTypeId
                         && r.Enabled)
                .OrderByDescending(r => r.ActionTimestamp)
                .FirstOrDefaultAsync(ct);
            if (overrideRule is not null) return overrideRule;
        }

        // ── Fall back to JobType-level default ───────────────────────────
        // Filtered unique index on (JobTypeId, ErrorTypeId) WHERE Enabled=1
        // AND MonitoredJobId IS NULL guarantees at most one row.
        return await db.FixPolicyRules
            .Include(r => r.ErrorType)
            .Include(r => r.Steps.OrderBy(s => s.StepOrder))
            .AsSplitQuery()
            .Where(r => r.JobTypeId      == jobTypeId
                     && r.ErrorTypeId    == errorTypeId
                     && r.MonitoredJobId == null
                     && r.Enabled)
            .OrderByDescending(r => r.ActionTimestamp)
            .FirstOrDefaultAsync(ct);
    }
}
